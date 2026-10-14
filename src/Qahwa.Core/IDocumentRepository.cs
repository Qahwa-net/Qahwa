namespace Qahwa.Core;

public sealed record ChildUpdate(string Name, IReadOnlyDictionary<string, object?> Values);

/// <summary>The row-level changes for one child table of a document, applied together with the parent.</summary>
public sealed record ChildChanges(
    DocTypeMeta Meta,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Inserts,
    IReadOnlyList<ChildUpdate> Updates,
    IReadOnlyList<string> Deletes)
{
    public bool IsEmpty => Inserts.Count == 0 && Updates.Count == 0 && Deletes.Count == 0;
}

/// <summary>
/// Persistence for documents. Implementations receive already validated, storage-typed values
/// and must still treat column names as untrusted (see the whitelist in <see cref="DocTypeMetaExtensions.ColumnTypes"/>).
/// </summary>
public interface IDocumentRepository
{
    /// <summary>Inserts the document and its child rows atomically: either everything is saved or nothing is.</summary>
    /// <exception cref="ConflictException">A unique constraint (name or unique field) was violated.</exception>
    Task InsertAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ChildChanges>? children = null,
        CancellationToken ct = default);

    Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default);

    /// <summary>The rows of one child table of a document, in display order.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetChildrenAsync(
        DocTypeMeta childMeta,
        string parentDocType,
        string parent,
        string parentField,
        CancellationToken ct = default);

    /// <summary>Updates the document and applies the child row changes atomically.</summary>
    /// <param name="expectedModified">When set, the update only applies if the stored value still matches.</param>
    /// <returns>False when no row matched (missing, or modified concurrently); nothing is changed then.</returns>
    Task<bool> UpdateAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified,
        IReadOnlyList<ChildChanges>? children = null,
        CancellationToken ct = default);

    /// <summary>Deletes the document together with all of its rows in <paramref name="childTables"/>, atomically.</summary>
    Task<bool> DeleteAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyList<DocTypeMeta>? childTables = null,
        CancellationToken ct = default);

    Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default);

    Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default);

    Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default);

    /// <summary>Atomically increments and returns the counter for a naming series prefix.</summary>
    Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default);
}
