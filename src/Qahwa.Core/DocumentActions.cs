using System.Globalization;
using Qahwa.Abstractions;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>
/// No-code automation: "when a document of this DocType is created, changed, submitted or cancelled, and a condition holds, set a field
/// or create another document". Actions are ordinary Document Action documents. There is no code and no expression that can reach
/// beyond the document: values are text with <c>{{field}}</c> placeholders, targets must be plain fields.
///
/// Actions run as the person who made the change, never with more rights than they have, and a change made by an action does not
/// start further actions (so they cannot loop). An action that cannot run never fails the save; its problem is written on the action.
/// </summary>
public sealed class DocumentActionEngine(IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, TimeProvider clock)
{
    public const string DocTypeName = "Document Action";
    public const int MaxPerEvent = 20;
    public const int MaxMappingLines = 30;
    public const int MaxText = 500;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private static readonly string[] Events = ["New", "Update", "ValueChange", "Submit", "Cancel"];


    private static readonly AsyncLocal<bool> Running = new();

    private record Snapshot(DateTime At, ILookup<string, Dictionary<string, object?>> Actions);

    private volatile Snapshot? _snapshot;

    /// <summary>Forget the cached actions (called when an action is saved or deleted on this server; others catch up within seconds).</summary>
    public void Invalidate() => _snapshot = null;

    private async Task<IReadOnlyList<Dictionary<string, object?>>> ActionsForAsync(string doctype, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var snapshot = _snapshot;
        if (snapshot is null || now - snapshot.At > CacheFor)
        {
            var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery
            {
                Filters = [new QueryFilter("active", FilterOp.Eq, true)], OrderBy = "creation", Descending = false, Limit = 1000,
            }, ct);
            snapshot = new Snapshot(now, rows.ToLookup(r => r["reference_doctype"] as string ?? string.Empty, StringComparer.Ordinal));
            _snapshot = snapshot;
        }

        return snapshot.Actions[doctype].ToList();
    }

    public async Task AfterSaveAsync(
        DocTypeMeta meta, string docName, bool isNew, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?> after, IUserContext actor, CancellationToken ct = default)
    {
        if (Running.Value || !AssignmentRuleManager.Applies(meta))
            return;

        try
        {
            var actions = await ActionsForAsync(meta.Name, ct);
            if (actions.Count == 0)
                return;

            var changed = before is null ? [] : ChangedFields(meta, before, after);
            var oldStatus = Convert.ToInt32(before?.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            var newStatus = Convert.ToInt32(after.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            foreach (var action in actions.Take(MaxPerEvent))
            {
                var applies = (action["event"] as string) switch
                {
                    "New" => isNew,
                    "Update" => !isNew && changed.Count > 0,
                    "ValueChange" => !isNew && action["watch_field"] is string watch && changed.Contains(watch),
                    "Submit" => !isNew && oldStatus == 0 && newStatus == 1,
                    "Cancel" => !isNew && oldStatus == 1 && newStatus == 2,
                    _ => false,
                };
                if (!applies || !documents.EvaluateCondition(action["condition"] as string, meta, after, actor))
                    continue;

                Running.Value = true;
                try
                {
                    await RunAsync(meta, docName, action, after, actor, ct);
                }
                finally
                {
                    Running.Value = false;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is already saved; an automation problem must not turn that into an error.
        }
    }

    private async Task RunAsync(DocTypeMeta meta, string docName, Dictionary<string, object?> action, IReadOnlyDictionary<string, object?> doc, IUserContext actor, CancellationToken ct)
    {
        string? problem = null;
        try
        {
            problem = Check(meta, action);
            if (problem is null)
            {
                object? Value(string name) => name switch
                {
                    "name" => docName, "doctype" => meta.Name, "user" => actor.User,
                    _ => Values.FromStorage(doc.GetValueOrDefault(name)),
                };

                if (action["action"] as string == "Set Field")
                {
                    var field = (string)action["target_field"]!;
                    await documents.UpdateAsync(meta.Name, docName, new Dictionary<string, object?> { [field] = MessageTemplate.Render((string)action["value"]!, Value) }, actor, ct);
                }
                else
                {
                    var values = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (field, template) in Mapping((string?)action["mapping"]))
                        values[field] = MessageTemplate.Render(template, Value);
                    await documents.InsertAsync((string)action["target_doctype"]!, values, actor, ct);
                }
            }
        }
        catch (QahwaException e)
        {
            problem = e is DocumentValidationException v ? string.Join(" ", v.Errors.Select(x => string.IsNullOrEmpty(x.Field) ? x.Message : $"{x.Field}: {x.Message}")) : e.Message;
        }

        var name = (string)action["name"]!;
        var text = problem is null ? null : (problem.Length > 500 ? problem[..500] : problem);
        var previous = action["last_error"] as string;
        var runs = Convert.ToInt32(action["run_count"] ?? 0, CultureInfo.InvariantCulture) + (problem is null ? 1 : 0);
        var write = problem is null ? previous is not null || runs % 25 == 1 : previous != text;
        if (write)
        {
            // Status is kept by the engine itself (the fields are read-only for people), without starting any automation.
            var utc = clock.GetUtcNow().UtcDateTime;
            await repository.UpdateAsync(registry.Get(DocTypeName), name,
                new Dictionary<string, object?> { ["last_error"] = text, ["run_count"] = runs, ["modified"] = new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc) }, null, null, ct);
            Invalidate();
        }
    }

    private static IEnumerable<(string Field, string Template)> Mapping(string? text)
    {
        foreach (var line in (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = line.IndexOf('=', StringComparison.Ordinal);
            if (at > 0)
                yield return (line[..at].Trim(), line[(at + 1)..].Trim());
        }
    }

    private static bool Settable(DocFieldMeta f) =>
        FieldTypes.HasColumn(f.Type) && f.Type is not (FieldType.Password or FieldType.Attach or FieldType.AttachImage or FieldType.Table) && f.Formula is null;

    /// <summary>What is wrong with an action, in words (null when it can run). Checked every time it runs, whatever way it was written.</summary>
    public string? Check(DocTypeMeta meta, IReadOnlyDictionary<string, object?> action)
    {
        if (action["reference_doctype"] as string != meta.Name)
            return "The action belongs to another DocType.";
        if (!Events.Contains(action["event"] as string))
            return "Choose when the action runs.";
        if (action["event"] as string is "Submit" or "Cancel" && !meta.IsSubmittable)
            return $"{meta.Name} does not support submission. Use ValueChange with its workflow field, or New / Update.";
        if (action["event"] as string == "ValueChange" && meta.Fields.All(f => f.Name != action["watch_field"] as string))
            return "Name a field of this DocType to watch.";

        var names = ValueNames(meta);
        if (action["action"] as string == "Set Field")
        {
            var target = meta.Fields.FirstOrDefault(f => f.Name == action["target_field"] as string);
            if (target is null || !Settable(target))
                return "Choose a plain field of this DocType to set.";
            return TemplateProblem(action["value"] as string, names);
        }

        if (action["action"] as string == "Create Document")
        {
            if (registry.SourceOf(action["target_doctype"] as string ?? string.Empty) is not { } source || source == DocTypeSource.Core)
                return "Choose a DocType to create (not a framework DocType).";
            var target = registry.Get((string)action["target_doctype"]!);
            if (target.IsChildTable || target.IsSingle || target.Module == "Core")
                return "Choose a DocType to create (not a child table or settings page).";
            var lines = Mapping(action["mapping"] as string).ToList();
            var rawLines = (action["mapping"] as string ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (rawLines.Any(line => line.IndexOf('=', StringComparison.Ordinal) <= 0))
                return "Every mapping line must use target_field = value.";
            if (lines.Select(line => line.Field).Distinct(StringComparer.Ordinal).Count() != lines.Count)
                return "Map each target field only once.";
            if (lines.Count == 0 || lines.Count > MaxMappingLines)
                return $"Map between 1 and {MaxMappingLines} fields, one per line, like  customer = {{{{customer}}}}.";
            foreach (var (field, template) in lines)
            {
                if (!target.Fields.Any(f => f.Name == field && Settable(f)))
                    return $"'{field}' is not a plain field of {target.Name}.";
                if (TemplateProblem(template, names) is { } bad)
                    return bad;
            }

            return null;
        }

        return "Choose what the action does.";
    }

    private static HashSet<string> ValueNames(DocTypeMeta meta) =>
        meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type != FieldType.Password).Select(f => f.Name).Concat(MessageTemplate.Specials).ToHashSet(StringComparer.Ordinal);

    private static string? TemplateProblem(string? template, HashSet<string> names)
    {
        if (string.IsNullOrEmpty(template) || template.Length > MaxText)
            return $"Write a value of up to {MaxText} characters.";
        if (MessageTemplate.HasMalformed(template))
            return "Use {{field}} with a plain field name.";
        return MessageTemplate.Names(template).FirstOrDefault(n => !names.Contains(n)) is { } bad ? $"'{bad}' is not a field of this DocType." : null;
    }

    private static HashSet<string> ChangedFields(DocTypeMeta meta, IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
            if (!Equals(Normalize(before.GetValueOrDefault(field.Name)), Normalize(after.GetValueOrDefault(field.Name))))
                changed.Add(field.Name);
        if (!Equals(Normalize(before.GetValueOrDefault("docstatus")), Normalize(after.GetValueOrDefault("docstatus"))))
            changed.Add("docstatus");
        return changed;
    }

    private static string? Normalize(object? value) => value switch
    {
        null => null,
        string s when s.Length == 0 => null,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
