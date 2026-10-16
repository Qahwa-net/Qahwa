using System.Globalization;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

public sealed record ViewerInfo(string User, string? FullName, long Views, DateTime LastViewed);

/// <summary>
/// View Log: for DocTypes that turn on "track views", one row per person and document says who opened it, how many times and when
/// they last did. It is for the people who may change the document ("seen by"), not for everyone. A person's own view is recorded
/// at most once every ten minutes, so reloading a page does not become a write storm, and a failure to record never stops a read.
/// </summary>
public sealed class ViewLogService(IMetaRegistry registry, IDocumentRepository repository, PermissionService permissions, Lazy<DocumentService> documents, NotificationService notifications, HistoryRecorder history)
{
    public const string DocTypeName = "View Log";
    public const int MaxViewers = 100;
    public static readonly TimeSpan Throttle = TimeSpan.FromMinutes(10);

    private DocTypeMeta? Meta => registry.TryGet(DocTypeName, out var meta) ? meta : null;

    public static bool Tracks(DocTypeMeta meta) => meta.TrackViews && AssignmentRuleManager.Applies(meta);

    /// <summary>Notes that <paramref name="user"/> opened the document. Never throws.</summary>
    public async Task RecordAsync(DocTypeMeta meta, string name, IUserContext user, CancellationToken ct = default)
    {
        if (!Tracks(meta) || !user.IsAuthenticated || Meta is not { } log)
            return;
        try
        {
            var now = history.Now();
            var rows = await repository.ListAsync(log, new ListQuery
            {
                Filters = [new("ref_doctype", FilterOp.Eq, meta.Name), new("ref_docname", FilterOp.Eq, name), new("user", FilterOp.Eq, user.User)],
                Limit = 1,
            }, ct);
            if (rows.Count == 0)
            {
                var row = HistoryRecorder.Standard(user.User, now);
                row["ref_doctype"] = meta.Name;
                row["ref_docname"] = name;
                row["user"] = user.User;
                row["views"] = 1L;
                row["last_viewed"] = now;
                await repository.InsertAsync(log, row, null, ct);
                return;
            }

            var existing = rows[0];
            if (existing.GetValueOrDefault("last_viewed") is DateTime last && now - last < Throttle)
                return;
            var version = (DateTime)existing["modified"]!;
            await repository.UpdateAsync(log, (string)existing["name"]!, new Dictionary<string, object?>
            {
                ["views"] = Convert.ToInt64(existing.GetValueOrDefault("views") ?? 0L, CultureInfo.InvariantCulture) + 1,
                ["last_viewed"] = now,
                ["modified"] = now > version ? now : version.AddTicks(10),
            }, version, null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the log is a convenience; losing a line must not stop anyone from reading
        }
    }

    /// <summary>Who has opened the document, most recent first. Needs the right to change it (and, of course, to read it).</summary>
    public async Task<IReadOnlyList<ViewerInfo>> ViewersAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        await documents.Value.GetAsync(doctype, name, user, ct);
        permissions.Require(meta, user, PermAction.Write);
        if (!Tracks(meta) || Meta is not { } log)
            return [];
        var rows = await repository.ListAsync(log, new ListQuery
        {
            Filters = [new("ref_doctype", FilterOp.Eq, meta.Name), new("ref_docname", FilterOp.Eq, name)],
            OrderBy = "last_viewed", Descending = true, Limit = MaxViewers,
        }, ct);
        var list = new List<ViewerInfo>();
        foreach (var row in rows)
        {
            var person = (string)row["user"]!;
            list.Add(new ViewerInfo(person, await notifications.NameOfAsync(person, ct), Convert.ToInt64(row.GetValueOrDefault("views") ?? 0L, CultureInfo.InvariantCulture), (DateTime)row["last_viewed"]!));
        }

        return list;
    }
}
