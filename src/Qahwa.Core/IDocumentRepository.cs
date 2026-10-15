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

/// <summary>One place that stores the name of another document: set <see cref="Field"/> to the new name where it holds the old one.</summary>
/// <param name="WhereField">Only rows where this column equals <paramref name="WhereValue"/> are updated (for text references that name a DocType).</param>
public sealed record ReferenceUpdate(DocTypeMeta Meta, string Field, string? WhereField = null, string? WhereValue = null);

/// <summary>How many documents have one value of a field.</summary>
public sealed record GroupCount(object? Value, long Count);

/// <summary>A naming series counter: the last number handed out for a prefix.</summary>
public sealed record SeriesCounter(string Prefix, long Current);

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
    /// <param name="sideWrites">Rows to insert into other tables in the same transaction (the updates log).</param>
    Task<bool> DeleteAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyList<DocTypeMeta>? childTables = null,
        CancellationToken ct = default,
        IReadOnlyList<ChildChanges>? sideWrites = null);

    Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default);

    Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default);

    Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default);

    /// <summary>The most common values of one field among the documents matching the filters, with how many each has (most first).</summary>
    Task<IReadOnlyList<GroupCount>> GroupCountAsync(
        DocTypeMeta meta, string field, IReadOnlyList<QueryFilter> filters, int limit, CancellationToken ct = default);

    /// <summary>Atomically increments and returns the counter for a naming series prefix.</summary>
    Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default);

    Task<IReadOnlyList<SeriesCounter>> ListSeriesAsync(CancellationToken ct = default);

    /// <summary>Sets the counter for a prefix (the next document gets <paramref name="current"/> + 1).</summary>
    Task SetSeriesAsync(string prefix, long current, CancellationToken ct = default);

    /// <summary>
    /// Renames a document, or merges it into <paramref name="newName"/> (which must already exist): its child rows follow it,
    /// every reference to it is updated, and with a merge the old document is removed. All in one transaction.
    /// </summary>
    /// <exception cref="ConflictException">The new name (or a unique value) is already taken.</exception>
    Task RenameAsync(
        DocTypeMeta meta,
        string oldName,
        string newName,
        bool merge,
        IReadOnlyList<DocTypeMeta> childTables,
        IReadOnlyList<ReferenceUpdate> references,
        CancellationToken ct = default,
        IReadOnlyList<ChildChanges>? sideWrites = null);
}
