using System.Text.RegularExpressions;
using Qahwa.Core;

namespace Qahwa.Testing;

/// <summary>
/// A dictionary-backed repository for unit and API tests. It mirrors the semantics the document engine
/// relies on (unique names and fields, concurrency check on update, atomic parent + child writes)
/// without needing a database.
/// </summary>
public sealed class InMemoryDocumentRepository : IDocumentRepository
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, object?>>> _tables =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, long> _series = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task InsertAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ChildChanges>? children = null,
        CancellationToken ct = default)
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

            // Check everything first so a failure leaves nothing behind, like a rolled back transaction.
            foreach (var change in children ?? [])
            {
                var childTable = Table(change.Meta);
                var names = change.Inserts.Select(r => (string)r["name"]!).ToList();
                if (names.Distinct().Count() != names.Count || names.Any(childTable.ContainsKey))
                    throw new ConflictException($"A {meta.Name} with the same name or a unique value already exists.");
            }

            table[name] = Row(meta, values);
            ApplyChildren(children);
        }

        return Task.CompletedTask;
    }

    public Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(Table(meta).TryGetValue(name, out var row) ? new Dictionary<string, object?>(row) : null);
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetChildrenAsync(
        DocTypeMeta childMeta, string parentDocType, string parent, string parentField, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var rows = Table(childMeta).Values
                .Where(r => Equals(r["parent"], parent) && Equals(r["parenttype"], parentDocType) && Equals(r["parentfield"], parentField))
                .OrderBy(r => Convert.ToInt32(r["idx"]))
                .Select(r => new Dictionary<string, object?>(r))
                .ToList();
            return Task.FromResult<IReadOnlyList<Dictionary<string, object?>>>(rows);
        }
    }

    public Task<bool> UpdateAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified,
        IReadOnlyList<ChildChanges>? children = null,
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
            ApplyChildren(children);
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(
        DocTypeMeta meta, string name, IReadOnlyList<DocTypeMeta>? childTables = null, CancellationToken ct = default,
        IReadOnlyList<ChildChanges>? sideWrites = null)
    {
        lock (_gate)
        {
            foreach (var child in (childTables ?? []).DistinctBy(c => c.Name))
            {
                var table = Table(child);
                foreach (var key in table.Where(kv => Equals(kv.Value["parent"], name) && Equals(kv.Value["parenttype"], meta.Name)).Select(kv => kv.Key).ToList())
                    table.Remove(key);
            }

            var deleted = Table(meta).Remove(name);
            if (deleted)
                ApplyChildren(sideWrites);
            return Task.FromResult(deleted);
        }
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

    public Task<IReadOnlyList<GroupCount>> GroupCountAsync(
        DocTypeMeta meta, string field, IReadOnlyList<QueryFilter> filters, int limit, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var groups = Filter(meta, new ListQuery { Filters = filters })
                .GroupBy(r => r.GetValueOrDefault(field))
                .Select(g => new GroupCount(g.Key, g.Count()))
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Value?.ToString(), StringComparer.Ordinal)
                .Take(limit)
                .ToList();
            return Task.FromResult<IReadOnlyList<GroupCount>>(groups);
        }
    }

    public Task<IReadOnlyList<SeriesCounter>> ListSeriesAsync(CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SeriesCounter>>(_series.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new SeriesCounter(kv.Key, kv.Value)).ToList());
    }

    public Task SetSeriesAsync(string prefix, long current, CancellationToken ct = default)
    {
        lock (_gate)
            _series[prefix] = current;
        return Task.CompletedTask;
    }

    public Task RenameAsync(
        DocTypeMeta meta,
        string oldName,
        string newName,
        bool merge,
        IReadOnlyList<DocTypeMeta> childTables,
        IReadOnlyList<ReferenceUpdate> references,
        CancellationToken ct = default,
        IReadOnlyList<ChildChanges>? sideWrites = null)
    {
        lock (_gate)
        {
            var table = Table(meta);
            if (!table.ContainsKey(oldName))
                throw new NotFoundException($"{meta.Name} '{oldName}' was not found.");
            if (!merge && table.ContainsKey(newName))
                throw new ConflictException($"A {meta.Name} named '{newName}' or with the same unique value already exists.");

            foreach (var reference in references)
            {
                foreach (var row in Table(reference.Meta).Values)
                {
                    if (Equals(row.GetValueOrDefault(reference.Field), oldName)
                        && (reference.WhereField is null || Equals(row.GetValueOrDefault(reference.WhereField), reference.WhereValue)))
                    {
                        row[reference.Field] = newName;
                    }
                }
            }

            if (merge)
            {
                foreach (var child in childTables)
                {
                    var childTable = Table(child);
                    foreach (var key in childTable.Where(kv => Equals(kv.Value["parent"], oldName) && Equals(kv.Value["parenttype"], meta.Name)).Select(kv => kv.Key).ToList())
                        childTable.Remove(key);
                }

                table.Remove(oldName);
                ApplyChildren(sideWrites);
                return Task.CompletedTask;
            }

            var document = table[oldName];
            table.Remove(oldName);
            document["name"] = newName;
            table[newName] = document;
            foreach (var child in childTables)
            {
                foreach (var row in Table(child).Values.Where(r => Equals(r["parent"], oldName) && Equals(r["parenttype"], meta.Name)))
                    row["parent"] = newName;
            }

            ApplyChildren(sideWrites);
        }

        return Task.CompletedTask;
    }

    private void ApplyChildren(IReadOnlyList<ChildChanges>? children)
    {
        foreach (var change in children ?? [])
        {
            var table = Table(change.Meta);
            foreach (var name in change.Deletes)
                table.Remove(name);
            foreach (var update in change.Updates)
            {
                if (!table.TryGetValue(update.Name, out var row))
                    continue;
                foreach (var (key, value) in update.Values)
                    row[key] = value;
            }

            foreach (var insert in change.Inserts)
                table[(string)insert["name"]!] = Row(change.Meta, insert);
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
        Table(meta).Values.Where(row => query.Filters.All(f => Holds(f, row)));

    private bool Holds(QueryFilter f, Dictionary<string, object?> row) => f.Op switch
    {
        FilterOp.Marked => IsMarked(f, row.GetValueOrDefault("name") as string),
        FilterOp.ChildMatches => HasMatchingChild((ChildMatch)f.Value!, row.GetValueOrDefault("name") as string),
        _ => Matches(row.GetValueOrDefault(f.Field), f, row.GetValueOrDefault("name") as string),
    };

    private bool HasMatchingChild(ChildMatch match, string? name) =>
        Table(match.Child).Values.Any(r => Equals(r.GetValueOrDefault("parent"), name) && Equals(r.GetValueOrDefault("parenttype"), match.ParentDocType)
            && Equals(r.GetValueOrDefault("parentfield"), match.ParentField) && match.Inner.All(i => Holds(i, r)));

    private bool IsMarked(QueryFilter filter, string? name)
    {
        var marked = (MarkedBy)filter.Value!;
        return Table(marked.Table).Values.Any(r => Equals(r.GetValueOrDefault("ref_doctype"), marked.RefDocType) && Equals(r.GetValueOrDefault("ref_docname"), name) && Equals(r.GetValueOrDefault(marked.Field), marked.Value));
    }

    /// <summary>A LIKE pattern as the real databases read it: % and _ are wildcards and a backslash makes the next character literal.</summary>
    private static string LikeToRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
                sb.Append(Regex.Escape(pattern[++i].ToString()));
            else if (c == '%')
                sb.Append(".*");
            else if (c == '_')
                sb.Append('.');
            else
                sb.Append(Regex.Escape(c.ToString()));
        }

        return sb.Append('$').ToString();
    }

    private static bool Matches(object? actual, QueryFilter filter, string? name = null)
    {
        switch (filter.Op)
        {
            case FilterOp.IsNull: return actual is null;
            case FilterOp.IsNotNull: return actual is not null;
            case FilterOp.OwnerOrNames: { var o = (OwnerOrNames)filter.Value!; return Equals(actual, o.Owner) || o.Names.Contains(name); }
            case FilterOp.InOrNull: return actual is null || (filter.Value as IEnumerable<object?> ?? []).Any(v => Equals(v, actual));
            case FilterOp.In: return (filter.Value as IEnumerable<object?> ?? []).Any(v => Equals(v, actual));
            case FilterOp.NotIn: return actual is null || !(filter.Value as IEnumerable<object?> ?? []).Any(v => Equals(v, actual));
            case FilterOp.NotLike:
                return actual is not string t || !Regex.IsMatch(t, LikeToRegex((string)filter.Value!), RegexOptions.IgnoreCase | RegexOptions.Singleline);
            case FilterOp.Like:
                return actual is string s && Regex.IsMatch(s, LikeToRegex((string)filter.Value!), RegexOptions.IgnoreCase | RegexOptions.Singleline);
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
