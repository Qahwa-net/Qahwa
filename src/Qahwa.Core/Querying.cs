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
    IsNull,
    IsNotNull,
}

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
