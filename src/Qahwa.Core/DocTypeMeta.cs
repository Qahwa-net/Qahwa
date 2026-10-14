using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record DocTypeMeta
{
    public required string Name { get; init; }
    public string? Module { get; init; }
    public string? TitleField { get; init; }
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
}

public sealed record DocPermMeta
{
    public required string Role { get; init; }
    public bool Read { get; init; }
    public bool Write { get; init; }
    public bool Create { get; init; }
    public bool Delete { get; init; }
}
