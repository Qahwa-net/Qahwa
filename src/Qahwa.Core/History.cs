using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Builds the rows of the updates log (<c>Version</c>) and the system notes in a document's comment thread. The rows are
/// handed to the repository as side writes, so a change and its history are saved in the same transaction: there is
/// never a change without a record of it, or a record of a change that did not happen.
/// </summary>
public sealed class HistoryRecorder(IMetaRegistry registry, IDocumentRepository repository, TimeProvider clock)
{
    public const string VersionDocType = "Version";
    public const string CommentDocType = "Comment";

    public const int MaxContentLength = 5000;
    private const int MaxValueLength = 2000;
    private const int MaxFieldChanges = 200;
    private const int MaxTableRows = 100;
    private const int MaxSnapshotLength = 200_000;

    private const string Hidden = "•••";

    // Stored as text and shown through React (which escapes), so readable non-Latin text is kept as it is.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Whether changes to this DocType are logged: unless it opted out, and never for child tables (their parent carries the change) or the log itself.</summary>
    public bool Tracks(DocTypeMeta meta) =>
        meta.TrackChanges && !meta.IsChildTable && registry.TryGet(VersionDocType, out _);

    private static DateTime Truncate(DateTime utc) => new(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);

    public DateTime Now() => Truncate(clock.GetUtcNow().UtcDateTime);

    /// <summary>The standard columns every row has (name, owner, creation, ...), for rows the framework writes itself.</summary>
    public static Dictionary<string, object?> Standard(string user, DateTime now) => new()
    {
        ["name"] = Guid.NewGuid().ToString("N")[..10],
        ["creation"] = now,
        ["modified"] = now,
        ["modified_by"] = user,
        ["owner"] = user,
        ["docstatus"] = 0,
        ["idx"] = 0,
    };

    private static ChildChanges One(DocTypeMeta meta, Dictionary<string, object?> row) => new(meta, [row], [], []);

    /// <summary>One Version row, or null when this DocType is not tracked.</summary>
    public ChildChanges? Version(
        DocTypeMeta meta, string docName, string eventName, IReadOnlyDictionary<string, object?>? data, string user, DateTime now)
    {
        if (!Tracks(meta))
            return null;

        var row = Standard(user, now);
        row["ref_doctype"] = meta.Name;
        row["ref_docname"] = docName;
        row["event"] = eventName;
        row["data"] = data is null || data.Count == 0 ? null : JsonSerializer.Serialize(data, Json);
        return One(registry.Get(VersionDocType), row);
    }

    /// <summary>A system note ("Shared with Ann") in the comment thread, or null when comments are not available.</summary>
    public ChildChanges? InfoNote(string doctype, string docName, string text, string user, DateTime now) =>
        registry.TryGet(CommentDocType, out var meta) ? One(meta, CommentRow(doctype, docName, "Info", text, user, now)) : null;

    public Dictionary<string, object?> CommentRow(string doctype, string docName, string type, string content, string user, DateTime now)
    {
        var row = Standard(user, now);
        row["reference_doctype"] = doctype;
        row["reference_name"] = docName;
        row["comment_type"] = type;
        row["content"] = content;
        return row;
    }

    // ---- what changed ---------------------------------------------------------------------------------------

    /// <summary>A value as the log shows it: short, and with secrets hidden.</summary>
    private static object? Plain(DocFieldMeta? field, object? value)
    {
        if (value is null)
            return null;
        if (field?.Type == FieldType.Password)
            return Hidden;

        return value switch
        {
            DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            string s => s.Length > MaxValueLength ? s[..MaxValueLength] + "…" : s,
            _ => value,
        };
    }

    private static object?[] Entry(DocFieldMeta? field, string name, object? before, object? after) =>
        [name, Plain(field, before), Plain(field, after)];

    private static bool SameValue(object? a, object? b) => Equals(a, b) || (a is null or "") && (b is null or "");

    /// <summary>The field-level diff of an update: what each changed field held before and after, and what happened to table rows.</summary>
    public Dictionary<string, object?> UpdateData(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> existing,
        IReadOnlyDictionary<string, object?> changes,
        IReadOnlyList<(DocFieldMeta Field, DocTypeMeta Child, ChildChanges Change)> tableChanges,
        IReadOnlyDictionary<string, Dictionary<string, Dictionary<string, object?>>> currentRows)
    {
        var fields = meta.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var changed = changes
            .Where(kv => kv.Key is not ("modified" or "modified_by"))
            .Where(kv => !SameValue(existing.GetValueOrDefault(kv.Key), kv.Value))
            .Take(MaxFieldChanges)
            .Select(kv => Entry(fields.GetValueOrDefault(kv.Key), kv.Key, existing.GetValueOrDefault(kv.Key), kv.Value))
            .ToList();

        var data = new Dictionary<string, object?>();
        if (changed.Count > 0)
            data["changed"] = changed;

        var tables = new Dictionary<string, object?>();
        foreach (var (field, child, change) in tableChanges)
        {
            var childFields = child.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
            var rows = currentRows.GetValueOrDefault(field.Name) ?? [];
            var entry = new Dictionary<string, object?>();

            if (change.Inserts.Count > 0)
                entry["added"] = change.Inserts.Take(MaxTableRows).Select(r => RowValues(childFields, r)).ToList();
            if (change.Deletes.Count > 0)
                entry["removed"] = change.Deletes.Take(MaxTableRows)
                    .Select(n => rows.TryGetValue(n, out var old) ? RowValues(childFields, old) : new Dictionary<string, object?> { ["name"] = n }).ToList();

            var edited = change.Updates.Take(MaxTableRows).Select(u =>
            {
                var old = rows.GetValueOrDefault(u.Name);
                var diff = u.Values.Where(kv => kv.Key is not ("modified" or "modified_by" or "idx"))
                    .Where(kv => old is null || !SameValue(old.GetValueOrDefault(kv.Key), kv.Value))
                    .Select(kv => Entry(childFields.GetValueOrDefault(kv.Key), kv.Key, old?.GetValueOrDefault(kv.Key), kv.Value)).ToList();
                return (u.Name, diff);
            }).Where(x => x.diff.Count > 0)
              .Select(x => new Dictionary<string, object?> { ["name"] = x.Name, ["changed"] = x.diff }).ToList();
            if (edited.Count > 0)
                entry["changed"] = edited;

            if (entry.Count > 0)
                tables[field.Name] = entry;
        }

        if (tables.Count > 0)
            data["tables"] = tables;
        return data;
    }

    private static Dictionary<string, object?> RowValues(IReadOnlyDictionary<string, DocFieldMeta> fields, IReadOnlyDictionary<string, object?> row) =>
        row.Where(kv => fields.ContainsKey(kv.Key) && kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => Plain(fields[kv.Key], kv.Value));

    /// <summary>What a delete keeps so the document could be restored: its fields and table rows (dropped when huge).</summary>
    public Dictionary<string, object?> DeleteData(DocTypeMeta meta, IReadOnlyDictionary<string, object?> document)
    {
        var fields = meta.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var snapshot = new Dictionary<string, object?>();
        foreach (var (key, value) in document)
        {
            if (value is IEnumerable<Dictionary<string, object?>> rows && fields.TryGetValue(key, out var table))
                snapshot[key] = rows.Take(MaxTableRows).Select(r => r.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => Plain(null, kv.Value))).ToList();
            else if (value is not null)
                snapshot[key] = Plain(fields.GetValueOrDefault(key), value);
        }

        var data = new Dictionary<string, object?> { ["doc"] = snapshot };
        if (JsonSerializer.Serialize(data, Json).Length > MaxSnapshotLength)
            data["doc"] = snapshot.Where(kv => kv.Value is not System.Collections.IEnumerable || kv.Value is string).ToDictionary(kv => kv.Key, kv => kv.Value);
        return data;
    }

    // ---- housekeeping ----------------------------------------------------------------------------------------

    /// <summary>The comments of a document go with it. The updates log stays: it is the record of what happened.</summary>
    public async Task DeleteCommentsAsync(string doctype, string docName, CancellationToken ct = default)
    {
        if (!registry.TryGet(CommentDocType, out var meta))
            return;

        var rows = await repository.ListAsync(meta, new ListQuery
        {
            Filters = [new QueryFilter("reference_doctype", FilterOp.Eq, doctype), new QueryFilter("reference_name", FilterOp.Eq, docName)],
            Fields = ["name"],
            OrderBy = "name",
            Descending = false,
            Limit = 1000,
        }, ct);
        foreach (var row in rows)
            await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
    }

    /// <summary>References to a document's name in the log and the comments, for rename and merge.</summary>
    public IEnumerable<ReferenceUpdate> References(string doctype)
    {
        if (registry.TryGet(VersionDocType, out var version))
            yield return new ReferenceUpdate(version, "ref_docname", "ref_doctype", doctype);
        if (registry.TryGet(CommentDocType, out var comment))
            yield return new ReferenceUpdate(comment, "reference_name", "reference_doctype", doctype);
    }
}
