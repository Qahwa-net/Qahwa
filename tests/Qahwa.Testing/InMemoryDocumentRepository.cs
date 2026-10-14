using System.Text.RegularExpressions;
using Qahwa.Core;

namespace Qahwa.Testing;

/// <summary>
/// A dictionary-backed repository for unit and API tests. It mirrors the semantics the document engine
/// relies on (unique names and fields, concurrency check on update) without needing a database.
/// </summary>
public sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, object?>>> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, long> _series = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task InsertAsync(DocTypeMeta meta, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var table = Table(meta);
            var name = (string)values["name"]!;
            if (table.ContainsKey(name))
                throw new ConflictException($"A {meta.Name} with the same name or a unique value already exists.");

            foreach (var field in meta.Fields.Where(f => f.Unique))
            {
                var value = values.GetValueOrDefault(field.Name);
                if (value is not null && table.Values.Any(r => Equals(r.GetValueOrDefault(field.Name), value)))
                    throw new ConflictException($"A {meta.Name} with the same name or a unique value already exists.");
            }

            table[name] = Row(meta, values);
        }

        return Task.CompletedTask;
    }

    public Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Table(meta).TryGetValue(name, out var row) ? new Dictionary<string, object?>(row) : null);
    }

    public Task<bool> UpdateAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified,
        CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!Table(meta).TryGetValue(name, out var row))
                return Task.FromResult(false);
            if (expectedModified is not null && !Equals(row["modified"], expectedModified))
                return Task.FromResult(false);

            foreach (var (key, value) in values)
                row[key] = value;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(DocTypeMeta meta, string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Table(meta).Remove(name));
    }

    public Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Table(meta).ContainsKey(name));
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var order = query.OrderBy ?? "modified";
            var rows = Filter(meta, query).ToList();
            rows.Sort((a, b) =>
            {
                var c = Comparer<object?>.Create(Compare).Compare(a.GetValueOrDefault(order), b.GetValueOrDefault(order));
                if (query.Descending)
                    c = -c;
                return c != 0 ? c : string.CompareOrdinal((string)a["name"]!, (string)b["name"]!);
            });

            var page = rows.Skip(query.Offset).Take(query.Limit)
                .Select(r => query.Fields is null
                    ? new Dictionary<string, object?>(r)
                    : query.Fields.ToDictionary(f => f, f => r.GetValueOrDefault(f)))
                .ToList();
            return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(page);
        }
    }

    public Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult((long)Filter(meta, query).Count());
    }

    public Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _series[prefix] = _series.GetValueOrDefault(prefix) + 1;
            return Task.FromResult(_series[prefix]);
        }
    }

    private Dictionary<string, Dictionary<string, object?>> Table(DocTypeMeta meta)
    {
        if (!_tables.TryGetValue(meta.Name, out var table))
            _tables[meta.Name] = table = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        return table;
    }

    private static Dictionary<string, object?> Row(DocTypeMeta meta, IReadOnlyDictionary<string, object?> values)
    {
        var row = meta.ColumnTypes().Keys.ToDictionary(k => k, _ => (object?)null);
        foreach (var (key, value) in values)
            row[key] = value;
        return row;
    }

    private IEnumerable<Dictionary<string, object?>> Filter(DocTypeMeta meta, ListQuery query) =>
        Table(meta).Values.Where(row => query.Filters.All(f => Matches(row.GetValueOrDefault(f.Field), f)));

    private static bool Matches(object? actual, QueryFilter filter)
    {
        switch (filter.Op)
        {
            case FilterOp.IsNull: return actual is null;
            case FilterOp.IsNotNull: return actual is not null;
            case FilterOp.In: return (filter.Value as IEnumerable<object?> ?? []).Any(v => Equals(v, actual));
            case FilterOp.Like:
                var pattern = "^" + Regex.Escape((string)filter.Value!).Replace("%", ".*").Replace("_", ".") + "$";
                return actual is string s && Regex.IsMatch(s, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        if (actual is null)
            return false;

        var c = Compare(actual, filter.Value);
        return filter.Op switch
        {
            FilterOp.Eq => c == 0,
            FilterOp.Ne => c != 0,
            FilterOp.Gt => c > 0,
            FilterOp.Lt => c < 0,
            FilterOp.Ge => c >= 0,
            FilterOp.Le => c <= 0,
            _ => false,
        };
    }

    private static int Compare(object? a, object? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        (IComparable x, _) => x.CompareTo(b),
        _ => 0,
    };
}
