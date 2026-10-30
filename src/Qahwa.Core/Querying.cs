namespace Qahwa.Core;

public enum FilterOp
{
    Eq,
    Ne,
    Gt,
    Lt,
    Ge,
    Le,
    Like,
    In,

    /// <summary>Does not match the pattern (and an empty value counts as not matching it).</summary>
    NotLike,

    /// <summary>Not in the list (and an empty value counts as not in it).</summary>
    NotIn,

    /// <summary>In the list, or empty (used by User Permissions on Link fields).</summary>
    InOrNull,

    /// <summary>The column equals the owner, or the document's name is one of the shared names (see <see cref="OwnerOrNames"/>).</summary>
    OwnerOrNames,

    /// <summary>The document's name is listed in a table of labels (tags, likes) for this DocType and value (see <see cref="MarkedBy"/>).</summary>
    Marked,

    /// <summary>The document has a row in one of its child tables that matches every inner filter (see <see cref="ChildMatch"/>).</summary>
    ChildMatches,
    IsNull,
    IsNotNull,
}

/// <summary>The value of an <see cref="FilterOp.OwnerOrNames"/> filter: documents a user owns plus those shared with them.</summary>
public sealed record OwnerOrNames(string Owner, IReadOnlyList<string> Names);

/// <summary>The value of a <see cref="FilterOp.Marked"/> filter: documents that have a row in <paramref name="Table"/> (with <c>ref_doctype</c> and <c>ref_docname</c>) whose <paramref name="Field"/> is <paramref name="Value"/>.</summary>
public sealed record MarkedBy(DocTypeMeta Table, string RefDocType, string Field, string Value);

/// <summary>
/// The value of a <see cref="FilterOp.ChildMatches"/> filter: documents with at least one row of <paramref name="ParentField"/> (a Table field of
/// <paramref name="ParentDocType"/>) for which every <paramref name="Inner"/> filter holds, all on the same row. Inner filters are validated
/// like any other and refer to columns of the child DocType.
/// </summary>
public sealed record ChildMatch(DocTypeMeta Child, string ParentDocType, string ParentField, IReadOnlyList<QueryFilter> Inner);

/// <summary>A filter whose field and value have already been validated and converted to storage types.</summary>
public sealed record QueryFilter(string Field, FilterOp Op, object? Value = null);

/// <summary>A validated query: every field name is a real column and every value is a storage-typed value.</summary>
public sealed record ListQuery
{
    public IReadOnlyList<QueryFilter> Filters { get; init; } = [];
    public IReadOnlyList<string>? Fields { get; init; }
    public string? OrderBy { get; init; }
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 20;
    public int Offset { get; init; }
}

/// <summary>An unvalidated filter as received from a client, e.g. ["status", "=", "Open"].</summary>
public sealed record RawFilter(string Field, string Op, object? Value);

/// <summary>An unvalidated list request as received from a client.</summary>
public sealed record ListRequest
{
    public IReadOnlyList<RawFilter> Filters { get; init; } = [];
    public IReadOnlyList<string>? Fields { get; init; }
    public string? OrderBy { get; init; }
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 20;
    public int Offset { get; init; }
}

public sealed record ListResult(IReadOnlyList<Dictionary<string, object?>> Data, long Total);
