using System.Globalization;
using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <param name="Value">The value the field had (as the history log shows it).</param>
/// <param name="At">When the document got this value: its creation for the first one.</param>
/// <param name="By">Who made the change.</param>
/// <param name="AfterSeconds">How long after the previous milestone; null for the first one.</param>
public sealed record MilestoneItem(string Value, DateTime At, string By, long? AfterSeconds);

public sealed record MilestoneView(string Field, string Label, IReadOnlyList<MilestoneItem> Items);

/// <summary>
/// Milestones: a System Manager picks one field of a DocType (usually a status) in a Milestone Tracker, and every document of it
/// shows how that field moved: each value it held, when and who changed it, and how long each step took. Nothing extra is
/// stored: the milestones are read out of the updates log (Version), so they are always the real history, only as complete as the
/// log is (a DocType that turns the log off has none), and only what the reader may see.
/// </summary>
public sealed class MilestoneService(IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, PermissionService permissions)
{
    public const string DocTypeName = "Milestone Tracker";
    private const int MaxVersions = 1000;
    private const int MaxMilestones = 100;

    /// <summary>The rules a tracker must meet: a DocType that has documents of its own, and one of its fields that holds a plain value.</summary>
    public static void Validate(IMetaRegistry registry, IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        if (values.GetValueOrDefault("document_type") is not string doctype || values.GetValueOrDefault("track_field") is not string field)
            return;
        if (!registry.TryGet(doctype, out var meta))
        {
            errors.Add(new FieldError("document_type", $"There is no DocType named '{doctype}'."));
            return;
        }

        if (meta.IsChildTable || meta.IsSingle)
            errors.Add(new FieldError("document_type", "Milestones need a DocType with documents of their own (not a child table or settings page)."));
        else if (!meta.TrackChanges)
            errors.Add(new FieldError("document_type", $"{doctype} does not keep an updates log, so it has no history to read milestones from."));
        var target = meta.Fields.FirstOrDefault(f => f.Name == field);
        if (target is null)
            errors.Add(new FieldError("track_field", $"{doctype} has no field named '{field}'."));
        else if (!FieldTypes.HasColumn(target.Type) || target.Type is FieldType.Password or FieldType.Json or FieldType.Signature or FieldType.Attach or FieldType.AttachImage)
            errors.Add(new FieldError("track_field", $"A {target.Type} field cannot be tracked."));
    }

    private static string Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => string.Empty,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => value.ToString(),
    };

    /// <summary>The milestones of one document, or null when its DocType tracks nothing (or the reader may not see the tracked field).</summary>
    public async Task<MilestoneView?> ForDocumentAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        if (!registry.TryGet(DocTypeName, out var trackerMeta) || !registry.TryGet(doctype, out var meta))
            return null;
        // Reading the document is the least it takes (this also proves it exists and applies every permission layer).
        var doc = await documents.GetAsync(doctype, name, user, ct);

        var tracker = await repository.GetAsync(trackerMeta, doctype, ct);
        if (tracker?["track_field"] is not string field || meta.Fields.FirstOrDefault(f => f.Name == field) is not { } target)
            return null;
        var access = permissions.FieldAccessFor(meta, user, doc.GetValueOrDefault("owner") as string, creating: false);
        if (!access.CanRead(target) || !doc.ContainsKey(field))
            return null;

        var changes = new List<(string Before, string After, DateTime At, string By)>();
        if (registry.TryGet(HistoryRecorder.VersionDocType, out var versionMeta))
        {
            var rows = await repository.ListAsync(versionMeta, new ListQuery
            {
                Filters = [new("ref_doctype", FilterOp.Eq, doctype), new("ref_docname", FilterOp.Eq, name), new("event", FilterOp.Eq, "Update")],
                OrderBy = "creation", Descending = false, Limit = MaxVersions,
            }, ct);
            foreach (var row in rows)
            {
                if (row.GetValueOrDefault("data") is not string json)
                    continue;
                using var parsed = JsonDocument.Parse(json);
                if (!parsed.RootElement.TryGetProperty("changed", out var changed) || changed.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var entry in changed.EnumerateArray())
                    if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() == 3 && entry[0].GetString() == field)
                        changes.Add((Plain(entry[1]), Plain(entry[2]), (DateTime)row["creation"]!, (string)row["owner"]!));
            }
        }

        var items = new List<MilestoneItem>();
        var current = Convert.ToString(doc[field], CultureInfo.InvariantCulture) ?? string.Empty;
        var first = changes.Count > 0 ? changes[0].Before : current;
        if (first.Length > 0)
            items.Add(new MilestoneItem(first, (DateTime)doc["creation"]!, (string)doc["owner"]!, null));
        foreach (var (_, after, at, by) in changes)
        {
            if (after.Length == 0)
                continue;
            var seconds = items.Count > 0 ? (long?)Math.Max(0, (long)(at - items[^1].At).TotalSeconds) : null;
            items.Add(new MilestoneItem(after, at, by, seconds));
        }

        return new MilestoneView(field, target.Label ?? field, items.TakeLast(MaxMilestones).ToList());
    }
}
