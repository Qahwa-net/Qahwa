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

    public static IEnumerable<string> SelectOptions(this DocFieldMeta field) =>
        (field.Options ?? string.Empty)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
