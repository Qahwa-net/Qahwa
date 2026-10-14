using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Core.Designer;

namespace Qahwa.Testing;

public sealed class InMemoryDocTypeStore : IDocTypeStore
{
    private readonly Dictionary<string, StoredDocType> _rows = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<IReadOnlyList<StoredDocType>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StoredDocType>>(_rows.Values.OrderBy(r => r.Name, StringComparer.Ordinal).ToList());
    }

    public Task<StoredDocType?> GetAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.GetValueOrDefault(name));
    }

    public Task InsertAsync(StoredDocType row, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryAdd(row.Name, row))
                throw new ConflictException($"A DocType named '{row.Name}' already exists.");
        }

        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(StoredDocType row, DateTime expectedModified, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(row.Name, out var current) || current.Modified != expectedModified)
                return Task.FromResult(false);
            _rows[row.Name] = row;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.Remove(name));
    }
}

/// <summary>Tracks table columns in memory with the same additive-only rules as the SQL schema planner.</summary>
public sealed class InMemorySchemaManager : ISchemaManager
{
    private readonly Dictionary<string, Dictionary<string, FieldType>> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public bool HasTable(string doctype)
    {
        lock (_gate)
            return _tables.ContainsKey(doctype);
    }

    public Task<SchemaPreview> PreviewAsync(DocTypeMeta meta, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var wanted = meta.ColumnTypes();
            if (!_tables.TryGetValue(meta.Name, out var current))
                return Task.FromResult(new SchemaPreview(false, [new SchemaChange($"Create table tab{meta.Name}", "CREATE TABLE ...")], []));

            var changes = wanted.Where(c => !current.ContainsKey(c.Key))
                .Select(c => new SchemaChange($"Add column tab{meta.Name}.{c.Key}", "ALTER TABLE ... ADD COLUMN ..."))
                .ToList();
            var warnings = wanted.Where(c => current.TryGetValue(c.Key, out var type) && type != c.Value)
                .Select(c => $"tab{meta.Name}.{c.Key} has a different type in the database.")
                .ToList();
            return Task.FromResult(new SchemaPreview(true, changes, warnings));
        }
    }

    public Task ApplyAsync(DocTypeMeta meta, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_tables.TryGetValue(meta.Name, out var current))
                _tables[meta.Name] = current = new Dictionary<string, FieldType>();
            foreach (var (name, type) in meta.ColumnTypes())
                current.TryAdd(name, type);
        }

        return Task.CompletedTask;
    }

    public Task DropAsync(DocTypeMeta meta, CancellationToken ct = default)
    {
        lock (_gate)
            _tables.Remove(meta.Name);
        return Task.CompletedTask;
    }
}
