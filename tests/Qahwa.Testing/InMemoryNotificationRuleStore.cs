using Qahwa.Core;

namespace Qahwa.Testing;

public sealed class InMemoryNotificationRuleStore : INotificationRuleStore
{
    private readonly Dictionary<string, StoredNotificationRules> _rows = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>The stored version of a DocType's customisation, for tests that save twice.</summary>
    public DateTime? Modified(string docType)
    {
        lock (_gate)
            return _rows.TryGetValue(docType, out var row) ? row.Modified : null;
    }

    public Task<IReadOnlyList<StoredNotificationRules>> ListAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<StoredNotificationRules>>(_rows.Values.OrderBy(r => r.DocType, StringComparer.Ordinal).ToList());
    }

    public Task<StoredNotificationRules?> GetAsync(string docType, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.GetValueOrDefault(docType));
    }

    public Task<bool> SaveAsync(StoredNotificationRules row, DateTime? expectedModified, CancellationToken ct = default)
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
