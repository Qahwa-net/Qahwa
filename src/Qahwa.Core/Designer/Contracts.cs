namespace Qahwa.Core.Designer;

/// <summary>A DocType definition saved by the designer, exactly as the JSON that defines it.</summary>
public sealed record StoredDocType(
    string Name,
    string Definition,
    DateTime Creation,
    DateTime Modified,
    string Owner,
    string ModifiedBy);

public interface IDocTypeStore
{
    /// <summary>Returns an empty list when the table has not been created yet.</summary>
    Task<IReadOnlyList<StoredDocType>> ListAsync(CancellationToken ct = default);

    Task<StoredDocType?> GetAsync(string name, CancellationToken ct = default);

    /// <exception cref="ConflictException">A definition with this name already exists.</exception>
    Task InsertAsync(StoredDocType row, CancellationToken ct = default);

    /// <returns>False when the row is gone or was changed since <paramref name="expectedModified"/>.</returns>
    Task<bool> UpdateAsync(StoredDocType row, DateTime expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string name, CancellationToken ct = default);
}

public sealed record SchemaChange(string Description, string Sql);

/// <summary>What applying a DocType would do to the database.</summary>
public sealed record SchemaPreview(
    bool TableExists,
    IReadOnlyList<SchemaChange> Changes,
    IReadOnlyList<string> Warnings);

/// <summary>Column changes that are only ever made when asked for: a column is renamed, or its type is changed, never because the database merely differs.</summary>
public sealed record SchemaChanges(IReadOnlyList<(string From, string To)> Renames, IReadOnlySet<string> Retypes)
{
    public static readonly SchemaChanges None = new([], new HashSet<string>());

    public bool IsEmpty => Renames.Count == 0 && Retypes.Count == 0;
}

/// <summary>Creates, alters and drops the table behind a DocType. Implemented by the data layer.</summary>
public interface ISchemaManager
{
    /// <param name="changes">Renamed columns and columns whose type changes (by their new name); null for neither.</param>
    Task<SchemaPreview> PreviewAsync(DocTypeMeta meta, SchemaChanges? changes = null, CancellationToken ct = default);

    /// <exception cref="ConflictException">The change cannot be applied to the existing data (e.g. duplicates under a new unique field, values that do not fit a new type).</exception>
    Task ApplyAsync(DocTypeMeta meta, SchemaChanges? changes = null, CancellationToken ct = default);

    Task DropAsync(DocTypeMeta meta, CancellationToken ct = default);
}
