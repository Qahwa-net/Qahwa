namespace Qahwa.Core;

/// <summary>
/// Persistence for documents. Implementations receive already validated, storage-typed values
/// and must still treat column names as untrusted (see the whitelist in <see cref="DocTypeMetaExtensions.ColumnTypes"/>).
/// </summary>
public interface IDocumentRepository
{
    /// <exception cref="ConflictException">A unique constraint (name or unique field) was violated.</exception>
    Task InsertAsync(DocTypeMeta meta, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default);

    Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default);

    /// <param name="expectedModified">When set, the update only applies if the stored value still matches.</param>
    /// <returns>False when no row matched (missing, or modified concurrently).</returns>
    Task<bool> UpdateAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified,
        CancellationToken ct = default);

    Task<bool> DeleteAsync(DocTypeMeta meta, string name, CancellationToken ct = default);

    Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default);

    Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default);

    Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default);

    /// <summary>Atomically increments and returns the counter for a naming series prefix.</summary>
    Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default);
}
