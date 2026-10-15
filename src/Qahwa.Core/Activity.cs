using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qahwa.Core;

/// <summary>
/// One entry in a document's timeline: an update from the log (<see cref="Kind"/> "version") or a comment or system note
/// ("comment"). <see cref="Data"/> holds the field changes of a version, already limited to what the viewer may read.
/// </summary>
public sealed record ActivityItem(
    string Kind,
    string Id,
    string Event,
    string User,
    string? UserName,
    DateTime At,
    JsonNode? Data,
    string? Content,
    bool Edited,
    bool CanEdit);

/// <summary>
/// A document's updates log and comment thread: reading them (which needs read access to the document, and hides fields the
/// reader may not see) and the comments people write. Nobody can edit the log; comments can only be changed by their author.
/// </summary>
public sealed class ActivityService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    HistoryRecorder history,
    CollaborationHooks? collaboration = null)
{
    public const int MaxPage = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Proves the user may read the document (so history and comments are never a side door) and returns it.</summary>
    private async Task<(DocTypeMeta Meta, Dictionary<string, object?> Doc)> RequireReadableAsync(
        string doctype, string name, IUserContext user, CancellationToken ct)
    {
        var doc = await documents.GetAsync(doctype, name, user, ct);
        return (registry.Get(doctype), doc);
    }

    // ---- reading -------------------------------------------------------------------------------------------

    /// <summary>The merged, newest-first timeline of updates and comments. Pass the oldest <c>At</c> already shown as <paramref name="before"/> for the next page.</summary>
    public async Task<IReadOnlyList<ActivityItem>> TimelineAsync(
        string doctype, string name, IUserContext user, int limit = 50, DateTime? before = null, CancellationToken ct = default)
    {
        var (meta, doc) = await RequireReadableAsync(doctype, name, user, ct);
        limit = Math.Clamp(limit, 1, MaxPage);
        var access = permissions.FieldAccessFor(meta, user, doc.GetValueOrDefault("owner") as string, creating: false);

        var items = new List<ActivityItem>();
        if (registry.TryGet(HistoryRecorder.VersionDocType, out var versionMeta))
        {
            foreach (var row in await QueryAsync(versionMeta, [
                new("ref_doctype", FilterOp.Eq, doctype), new("ref_docname", FilterOp.Eq, name)], before, limit, ct))
            {
                var data = FilterVersion(meta, access, row.GetValueOrDefault("data") as string, registry);
                var eventName = (string)row["event"]!;
                // An update the reader may not see any part of is not shown; the document-level events always are.
                if (eventName is "Update" && data is null)
                    continue;
                items.Add(new ActivityItem("version", (string)row["name"]!, eventName, (string)row["owner"]!, null,
                    (DateTime)row["creation"]!, data, null, false, false));
            }
        }

        if (registry.TryGet(HistoryRecorder.CommentDocType, out var commentMeta))
        {
            foreach (var row in await QueryAsync(commentMeta, [
                new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)], before, limit, ct))
            {
                var type = row.GetValueOrDefault("comment_type") as string ?? "Comment";
                var author = (string)row["owner"]!;
                var created = (DateTime)row["creation"]!;
                items.Add(new ActivityItem("comment", (string)row["name"]!, type, author, null, created, null,
                    row.GetValueOrDefault("content") as string,
                    type == "Comment" && row.GetValueOrDefault("modified") is DateTime modified && modified > created,
                    type == "Comment" && string.Equals(author, user.User, StringComparison.Ordinal)));
            }
        }

        var page = items.OrderByDescending(i => i.At).ThenBy(i => i.Id, StringComparer.Ordinal).Take(limit).ToList();
        return await WithNamesAsync(page, ct);
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(
        DocTypeMeta meta, List<QueryFilter> filters, DateTime? before, int limit, CancellationToken ct)
    {
        if (before is not null)
            filters.Add(new QueryFilter("creation", FilterOp.Lt, before.Value));
        return await repository.ListAsync(meta, new ListQuery { Filters = filters, OrderBy = "creation", Descending = true, Limit = limit }, ct);
    }

    /// <summary>Shows people by their full name when there is one.</summary>
    private async Task<IReadOnlyList<ActivityItem>> WithNamesAsync(List<ActivityItem> items, CancellationToken ct)
    {
        if (!registry.TryGet("User", out var userMeta))
            return items;

        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var user in items.Select(i => i.User).Distinct(StringComparer.Ordinal))
            names[user] = (await repository.GetAsync(userMeta, user, ct))?.GetValueOrDefault("full_name") as string;
        return items.Select(i => i with { UserName = names.GetValueOrDefault(i.User) }).ToList();
    }

    /// <summary>
    /// The change data of a Version row without the fields (or tables) the reader may not read. Null when the row has nothing
    /// left to show. Fields that no longer exist are dropped, since their level cannot be known.
    /// </summary>
    private static JsonNode? FilterVersion(DocTypeMeta meta, FieldAccess access, string? json, IMetaRegistry registry)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        JsonObject? data;
        try
        {
            data = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (data is null)
            return null;

        var fields = meta.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        // Standard columns (docstatus) are part of the document itself, so anyone who reads it may see them change.
        bool Visible(string name) => StandardFields.Columns.ContainsKey(name) || (fields.TryGetValue(name, out var f) && access.CanRead(f));

        if (data["changed"] is JsonArray changed)
        {
            var kept = changed.Where(e => e is JsonArray { Count: 3 } a && a[0]?.GetValue<string>() is { } n && Visible(n)).Select(e => e!.DeepClone()).ToList();
            if (kept.Count == 0)
                data.Remove("changed");
            else
                data["changed"] = new JsonArray(kept.ToArray());
        }

        if (data["tables"] is JsonObject tables)
        {
            foreach (var key in tables.Select(kv => kv.Key).ToList())
            {
                if (!Visible(key))
                    tables.Remove(key);
            }

            if (tables.Count == 0)
                data.Remove("tables");
        }

        // "doc" is the snapshot kept for restoring a deleted document; it is never shown here.
        data.Remove("doc");
        return data.Count == 0 ? null : data;
    }

    /// <summary>
    /// How many comments each of these documents has, for the list view. Only documents the person may read are counted
    /// (the names are filtered through the ordinary list first), so this cannot show that an unreadable document has comments.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> CommentCountsAsync(
        string doctype, IReadOnlyCollection<string> names, IUserContext user, CancellationToken ct = default)
    {
        var wanted = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).Take(MaxPage).ToList();
        if (wanted.Count == 0 || !registry.TryGet(HistoryRecorder.CommentDocType, out var commentMeta))
            return new Dictionary<string, long>();

        var readable = (await documents.ListAsync(doctype, new ListRequest
        {
            Filters = [new RawFilter("name", "in", wanted)], Fields = ["name"], OrderBy = "name", Descending = false, Limit = MaxPage,
        }, user, ct)).Data.Select(r => (string)r["name"]!).ToList();
        if (readable.Count == 0)
            return new Dictionary<string, long>();

        var counts = await repository.GroupCountAsync(commentMeta, "reference_name", [
            new QueryFilter("reference_doctype", FilterOp.Eq, doctype),
            new QueryFilter("comment_type", FilterOp.Eq, "Comment"),
            new QueryFilter("reference_name", FilterOp.In, readable.Cast<object?>().ToList()),
        ], MaxPage, ct);
        return counts.Where(c => c.Value is string).ToDictionary(c => (string)c.Value!, c => c.Count, StringComparer.Ordinal);
    }

    // ---- comments --------------------------------------------------------------------------------------------

    private static string CheckContent(string? content)
    {
        content = (content ?? string.Empty).Trim();
        if (content.Length == 0 || content.Length > HistoryRecorder.MaxContentLength)
            throw new DocumentValidationException([new FieldError("content", $"Write a comment of 1 to {HistoryRecorder.MaxContentLength} characters.")]);
        if (content.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new DocumentValidationException([new FieldError("content", "The comment contains characters that are not allowed.")]);
        return content;
    }

    private DocTypeMeta CommentMeta() => registry.Get(HistoryRecorder.CommentDocType);

    /// <summary>Adds a comment. Needs read access to the document; a submitted or cancelled document can still be commented on.</summary>
    public async Task<ActivityItem> AddCommentAsync(string doctype, string name, string? content, IUserContext user, CancellationToken ct = default)
    {
        await RequireReadableAsync(doctype, name, user, ct);
        var text = CheckContent(content);
        var row = history.CommentRow(doctype, name, "Comment", text, user.User, history.Now());
        await repository.InsertAsync(CommentMeta(), row, null, ct);
        if (collaboration is not null)
            await collaboration.AfterCommentAsync(doctype, name, text, user, ct);
        return (await WithNamesAsync([ToItem(row, user)], ct))[0];
    }

    private static ActivityItem ToItem(Dictionary<string, object?> row, IUserContext user) =>
        new("comment", (string)row["name"]!, "Comment", (string)row["owner"]!, null, (DateTime)row["creation"]!, null,
            row["content"] as string, false, string.Equals((string)row["owner"]!, user.User, StringComparison.Ordinal));

    private async Task<Dictionary<string, object?>> RequireOwnCommentAsync(string doctype, string name, string id, IUserContext user, bool allowAdministrator, CancellationToken ct)
    {
        await RequireReadableAsync(doctype, name, user, ct);
        var row = await repository.GetAsync(CommentMeta(), id, ct);
        if (row is null
            || !string.Equals(row.GetValueOrDefault("reference_doctype") as string, doctype, StringComparison.Ordinal)
            || !string.Equals(row.GetValueOrDefault("reference_name") as string, name, StringComparison.Ordinal))
        {
            throw new NotFoundException("This comment was not found.");
        }

        var own = string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal);
        if (row.GetValueOrDefault("comment_type") as string != "Comment" || !(own || (allowAdministrator && permissions.IsAdministrator(user))))
            throw new PermissionDeniedException("You can only change your own comments.");
        return row;
    }

    public async Task<ActivityItem> UpdateCommentAsync(string doctype, string name, string id, string? content, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireOwnCommentAsync(doctype, name, id, user, allowAdministrator: false, ct);
        var text = CheckContent(content);
        var now = history.Now();
        var changes = new Dictionary<string, object?> { ["content"] = text, ["modified"] = now, ["modified_by"] = user.User };
        if (!await repository.UpdateAsync(CommentMeta(), id, changes, null, null, ct))
            throw new NotFoundException("This comment was not found.");

        row["content"] = text;
        row["modified"] = now;
        var item = ToItem(row, user) with { Edited = true };
        return (await WithNamesAsync([item], ct))[0];
    }

    /// <summary>A comment can be deleted by its author, or by an Administrator (moderation).</summary>
    public async Task DeleteCommentAsync(string doctype, string name, string id, IUserContext user, CancellationToken ct = default)
    {
        await RequireOwnCommentAsync(doctype, name, id, user, allowAdministrator: true, ct);
        await repository.DeleteAsync(CommentMeta(), id, null, ct);
    }
}
