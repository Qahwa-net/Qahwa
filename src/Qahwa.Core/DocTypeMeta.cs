using System.Runtime.CompilerServices;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record DocTypeMeta
{
    public required string Name { get; init; }
    public string? Module { get; init; }
    public string? TitleField { get; init; }

    /// <summary>
    /// How document names are generated: "hash" (default), "prompt" (caller supplies it),
    /// "field:&lt;fieldname&gt;", or a series pattern such as "CUST-.#####".
    /// </summary>
    public string? Autoname { get; init; }

    public bool IsChildTable { get; init; }
    public bool IsSubmittable { get; init; }
    public bool TrackChanges { get; init; }
    public IReadOnlyList<DocFieldMeta> Fields { get; init; } = [];
    public IReadOnlyList<DocPermMeta> Permissions { get; init; } = [];
    public IReadOnlyList<ValidationRule> Validations { get; init; } = [];
}

public sealed record DocFieldMeta
{
    public required string Name { get; init; }
    public required FieldType Type { get; init; }
    public string? Label { get; init; }

    /// <summary>Link target DocType, child DocType for Table, or newline-separated Select options.</summary>
    public string? Options { get; init; }

    public bool Required { get; init; }
    public bool Unique { get; init; }
    public bool InListView { get; init; }
    public string? Default { get; init; }

    /// <summary>Convert text values to lower case when saving (used for emails).</summary>
    public bool Lowercase { get; init; }

    /// <summary>Help text shown under the field in forms.</summary>
    public string? Description { get; init; }

    /// <summary>UI hint: the field is not shown in forms (its value is kept and still validated).</summary>
    public bool Hidden { get; init; }

    /// <summary>UI hint: the field is shown but cannot be edited in forms.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>Expression: the field is only shown (and only required) while this is true.</summary>
    public string? DependsOn { get; init; }

    /// <summary>Expression: the field is required while this is true, in addition to <see cref="Required"/>.</summary>
    public string? MandatoryDependsOn { get; init; }

    /// <summary>Expression: UI hint, the field cannot be edited while this is true.</summary>
    public string? ReadOnlyDependsOn { get; init; }

    /// <summary>Expression: the value is calculated from other fields every time the document is saved.</summary>
    public string? Formula { get; init; }

    /// <summary>"link_field.target_field": copy a value from the document a Link field points at.</summary>
    public string? FetchFrom { get; init; }

    /// <summary>With <see cref="FetchFrom"/>: only copy the value while this field is still empty.</summary>
    public bool FetchIfEmpty { get; init; }

    /// <summary>
    /// Permission level 0 to 9. Level 0 fields follow the DocType's ordinary access; a higher level is only visible
    /// and editable to roles that have a permission rule at that level.
    /// </summary>
    public int PermLevel { get; init; }
}

/// <summary>A rule on a whole document (or table row): while the expression is false, saving is refused with the message.</summary>
public sealed record ValidationRule
{
    public required string Expression { get; init; }
    public required string Message { get; init; }

    /// <summary>The field to show the message under; without it the message applies to the document.</summary>
    public string? Field { get; init; }
}

public sealed record DocPermMeta
{
    public required string Role { get; init; }
    public bool Read { get; init; }
    public bool Write { get; init; }
    public bool Create { get; init; }
    public bool Delete { get; init; }

    /// <summary>Restrict this rule to documents the user owns.</summary>
    public bool IfOwner { get; init; }

    /// <summary>The permission level of fields this rule covers (0 is the document itself and its ordinary fields).</summary>
    public int PermLevel { get; init; }
}

public static class DocTypeMetaExtensions
{
    private static readonly ConditionalWeakTable<DocTypeMeta, IReadOnlyDictionary<string, FieldType>> Cache = new();

    /// <summary>
    /// Every real column of the DocType's table (standard columns first), keyed by column name.
    /// This is the whitelist used for all dynamic SQL.
    /// </summary>
    public static IReadOnlyDictionary<string, FieldType> ColumnTypes(this DocTypeMeta meta) =>
        Cache.GetValue(meta, static m =>
        {
            var map = new Dictionary<string, FieldType>(StandardFields.Columns, StringComparer.Ordinal);
            if (m.IsChildTable)
            {
                foreach (var (name, type) in StandardFields.ChildColumns)
                    map[name] = type;
            }

            foreach (var field in m.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
                map[field.Name] = field.Type;
            return map;
        });

    private static readonly ConditionalWeakTable<DocTypeMeta, IReadOnlyList<DocFieldMeta>> FormulaCache = new();

    /// <summary>The calculated fields, ordered so every formula comes after the formulas it reads.</summary>
    public static IReadOnlyList<DocFieldMeta> FormulaFields(this DocTypeMeta meta) =>
        FormulaCache.GetValue(meta, static m =>
        {
            var pending = m.Fields.Where(f => f.Formula is not null).ToList();
            var names = pending.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            var dependencies = pending.ToDictionary(
                f => f.Name,
                f => Expressions.Expression.Parse(f.Formula!).Identifiers.Where(names.Contains).ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

            var ordered = new List<DocFieldMeta>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var ready = pending.Where(f => dependencies[f.Name].All(placed.Contains)).ToList();
                if (ready.Count == 0)
                    ready = [pending[0]]; // a cycle (rejected when a DocType is saved): fall back to field order

                foreach (var field in ready)
                {
                    ordered.Add(field);
                    placed.Add(field.Name);
                    pending.Remove(field);
                }
            }

            return ordered;
        });

    /// <summary>True when any field or rule of the DocType uses an expression.</summary>
    public static bool HasLogic(this DocTypeMeta meta) =>
        meta.Validations.Count > 0
        || meta.Fields.Any(f => f.Formula is not null || f.DependsOn is not null || f.MandatoryDependsOn is not null
                                || f.FetchFrom is not null || f.Default is { Length: > 1 } d && d.StartsWith('='));

    public static IEnumerable<string> SelectOptions(this DocFieldMeta field) =>
        (field.Options ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
