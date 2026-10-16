using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Access Log: an audit of data leaving through exports and printouts: who, which DocType (and document), what kind and how many rows.
/// It records only what really happened (after every permission check passed), is readable by System Managers, and never stops the
/// export or print it is describing.
/// </summary>
public sealed class AccessLogService(IMetaRegistry registry, IDocumentRepository repository, HistoryRecorder history)
{
    public const string DocTypeName = "Access Log";
    public const int DefaultRetentionDays = 365;

    private DocTypeMeta? Meta => registry.TryGet(DocTypeName, out var meta) ? meta : null;

    public async Task RecordAsync(string action, string doctype, string? name, long rows, IUserContext user, CancellationToken ct = default)
    {
        if (Meta is not { } meta || !user.IsAuthenticated)
            return;
        try
        {
            var row = HistoryRecorder.Standard(user.User, history.Now());
            row["user"] = user.User;
            row["action"] = action;
            row["ref_doctype"] = doctype.Length > 140 ? doctype[..140] : doctype;
            row["ref_docname"] = name is null ? null : name.Length > 140 ? name[..140] : name;
            row["rows"] = rows;
            await repository.InsertAsync(meta, row, null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // an audit line that cannot be written is reported by the database's own logs; it must not break the export
        }
    }

    public async Task<int> PurgeAsync(TimeSpan retention, CancellationToken ct = default)
    {
        if (Meta is not { } meta)
            return 0;
        var cutoff = history.Now() - retention;
        var removed = 0;
        while (true)
        {
            var old = await repository.ListAsync(meta, new ListQuery { Filters = [new("creation", FilterOp.Lt, cutoff)], Fields = ["name"], Limit = 200 }, ct);
            if (old.Count == 0)
                return removed;
            foreach (var row in old)
            {
                await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
                removed++;
            }
        }
    }
}
