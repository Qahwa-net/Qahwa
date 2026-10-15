using Qahwa.Core;

namespace Qahwa.Testing;

public sealed class InMemoryPermissionStore : IPermissionStore
{
    private readonly Dictionary<string, StoredPermissions> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public Task<IReadOnlyList<StoredPermissions>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StoredPermissions>>(_rows.Values.OrderBy(r => r.DocType, StringComparer.Ordinal).ToList());
    }

    public Task<StoredPermissions?> GetAsync(string docType, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.GetValueOrDefault(docType));
    }

    public Task<bool> SaveAsync(StoredPermissions row, DateTime? expectedModified, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (expectedModified is null)
                return Task.FromResult(_rows.TryAdd(row.DocType, row));

            if (!_rows.TryGetValue(row.DocType, out var current) || current.Modified != expectedModified)
                return Task.FromResult(false);

            _rows[row.DocType] = row;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(string docType, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.Remove(docType));
    }
}
