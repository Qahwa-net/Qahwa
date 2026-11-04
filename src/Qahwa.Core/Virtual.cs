using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>
/// Where the documents of a virtual DocType come from (ADR-158): a program reads them from outside the database (an API, a file, another
/// system). Register one in the container per virtual DocType. The query arrives already checked against the person's permissions and the
/// DocType's fields, with every value converted; <see cref="VirtualQuery"/> applies it to rows that are already in memory.
/// </summary>
public interface IVirtualSource
{
    /// <summary>The virtual DocType this source serves.</summary>
    string DocType { get; }

    Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(ListQuery query, CancellationToken ct);

    Task<long> CountAsync(ListQuery query, CancellationToken ct);

    /// <summary>One document by name, or null.</summary>
    Task<Dictionary<string, object?>?> GetAsync(string name, CancellationToken ct);
}

/// <summary>Applies a validated query to rows held in memory: for sources that fetch everything and let the framework filter, sort and page.</summary>
public static class VirtualQuery
{
    public static IReadOnlyList<Dictionary<string, object?>> Apply(IEnumerable<Dictionary<string, object?>> rows, ListQuery query)
    {
        var ordered = Filter(rows, query).ToList();
        var by = query.OrderBy ?? "name";
        ordered.Sort((a, b) => (query.Descending ? -1 : 1) * Compare(a.GetValueOrDefault(by), b.GetValueOrDefault(by)));
        return ordered.Skip(Math.Max(0, query.Offset)).Take(Math.Max(1, query.Limit)).ToList();
    }

    public static long Count(IEnumerable<Dictionary<string, object?>> rows, ListQuery query) => Filter(rows, query).LongCount();

    private static IEnumerable<Dictionary<string, object?>> Filter(IEnumerable<Dictionary<string, object?>> rows, ListQuery query) =>
        rows.Where(row => query.Filters.All(f => Holds(f, row)));

    private static bool Holds(QueryFilter filter, Dictionary<string, object?> row)
    {
        var actual = row.GetValueOrDefault(filter.Field);
        var list = filter.Value as IEnumerable<object?> ?? [];
        switch (filter.Op)
        {
            case FilterOp.IsNull: return actual is null;
            case FilterOp.IsNotNull: return actual is not null;
            case FilterOp.In: return list.Any(v => Compare(v, actual) == 0);
            case FilterOp.InOrNull: return actual is null || list.Any(v => Compare(v, actual) == 0);
            case FilterOp.NotIn: return actual is null || !list.Any(v => Compare(v, actual) == 0);
            case FilterOp.Like: return actual is string s && Like((string)filter.Value!).IsMatch(s);
            case FilterOp.NotLike: return actual is not string t || !Like((string)filter.Value!).IsMatch(t);
            case FilterOp.OwnerOrNames:
                var who = (OwnerOrNames)filter.Value!;
                return Equals(actual, who.Owner) || who.Names.Contains(row.GetValueOrDefault("name") as string);
        }

        if (filter.Op is FilterOp.Marked or FilterOp.ChildMatches)
            throw new DocumentValidationException("This DocType's documents come from outside, so they cannot be filtered by tags, likes or table rows.");
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

    /// <summary>A LIKE pattern as the databases read it: % and _ are wildcards and a backslash makes the next character literal.</summary>
    private static Regex Like(string pattern)
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

        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline, TimeSpan.FromSeconds(1));
    }

    private static int Compare(object? a, object? b) => (a, b) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (string x, string y) => string.Compare(x, y, StringComparison.OrdinalIgnoreCase),
        (IComparable x, _) when x.GetType() == b.GetType() => x.CompareTo(b),
        (IConvertible x, IConvertible y) when x is not string && y is not string => Convert.ToDecimal(x, System.Globalization.CultureInfo.InvariantCulture).CompareTo(Convert.ToDecimal(y, System.Globalization.CultureInfo.InvariantCulture)),
        _ => string.Compare(Convert.ToString(a, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(b, System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase),
    };
}

/// <summary>
/// The document store with virtual DocTypes added: for a virtual DocType, reads go to its <see cref="IVirtualSource"/> and anything that would
/// write is refused; every other DocType goes straight to the real store. Wrapping the store keeps lists, counts, searches, exports,
/// reports and the group-by sidebar working over virtual documents without each of them knowing.
/// </summary>
public sealed class VirtualAwareRepository(IDocumentRepository inner, IEnumerable<IVirtualSource> sources) : IDocumentRepository
{
    private readonly Dictionary<string, IVirtualSource> _sources = sources.ToDictionary(s => s.DocType, StringComparer.Ordinal);
    private const int MaxGroupRows = 10_000;

    private IVirtualSource SourceOf(DocTypeMeta meta) =>
        _sources.GetValueOrDefault(meta.Name) ?? throw new ConflictException($"{meta.Name} is a virtual DocType but nothing supplies its documents on this server.");

    private static ConflictException ReadOnly(DocTypeMeta meta) => new($"{meta.Name} is read-only: its documents come from outside.");

    public Task InsertAsync(DocTypeMeta meta, IReadOnlyDictionary<string, object?> values, IReadOnlyList<ChildChanges>? children = null, CancellationToken ct = default) =>
        meta.IsVirtual ? throw ReadOnly(meta) : inner.InsertAsync(meta, values, children, ct);

    public async Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        meta.IsVirtual ? await SourceOf(meta).GetAsync(name, ct) : await inner.GetAsync(meta, name, ct);

    public Task<IReadOnlyList<Dictionary<string, object?>>> GetChildrenAsync(DocTypeMeta childMeta, string parentDocType, string parent, string parentField, CancellationToken ct = default) =>
        inner.GetChildrenAsync(childMeta, parentDocType, parent, parentField, ct);

    public Task<bool> UpdateAsync(DocTypeMeta meta, string name, IReadOnlyDictionary<string, object?> values, DateTime? expectedModified, IReadOnlyList<ChildChanges>? children = null, CancellationToken ct = default) =>
        meta.IsVirtual ? throw ReadOnly(meta) : inner.UpdateAsync(meta, name, values, expectedModified, children, ct);

    public Task<bool> DeleteAsync(DocTypeMeta meta, string name, IReadOnlyList<DocTypeMeta>? childTables = null, CancellationToken ct = default, IReadOnlyList<ChildChanges>? sideWrites = null) =>
        meta.IsVirtual ? throw ReadOnly(meta) : inner.DeleteAsync(meta, name, childTables, ct, sideWrites);

    public async Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        meta.IsVirtual ? await SourceOf(meta).GetAsync(name, ct) is not null : await inner.ExistsAsync(meta, name, ct);

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default)
    {
        if (!meta.IsVirtual)
            return await inner.ListAsync(meta, query, ct);
        var rows = await SourceOf(meta).ListAsync(query, ct);
        // Like the real store, give back only the columns asked for (a source returns whole documents).
        return query.Fields is { Count: > 0 } fields
            ? rows.Select(r => fields.ToDictionary(f => f, f => r.GetValueOrDefault(f), StringComparer.Ordinal)).ToList()
            : rows;
    }

    public async Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default) =>
        meta.IsVirtual ? await SourceOf(meta).CountAsync(query, ct) : await inner.CountAsync(meta, query, ct);

    public async Task<IReadOnlyList<GroupCount>> GroupCountAsync(DocTypeMeta meta, string field, IReadOnlyList<QueryFilter> filters, int limit, CancellationToken ct = default)
    {
        if (!meta.IsVirtual)
            return await inner.GroupCountAsync(meta, field, filters, limit, ct);
        // The source has no group-by: count over the first rows it gives for these filters.
        var rows = await SourceOf(meta).ListAsync(new ListQuery { Filters = filters, Fields = [field], Limit = MaxGroupRows, Descending = false, OrderBy = "name" }, ct);
        return rows.GroupBy(r => r.GetValueOrDefault(field)).Select(g => new GroupCount(g.Key, g.LongCount())).OrderByDescending(g => g.Count).Take(Math.Max(1, limit)).ToList();
    }

    public Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default) => inner.NextSeriesAsync(prefix, ct);

    public Task<IReadOnlyList<SeriesCounter>> ListSeriesAsync(CancellationToken ct = default) => inner.ListSeriesAsync(ct);

    public Task SetSeriesAsync(string prefix, long current, CancellationToken ct = default) => inner.SetSeriesAsync(prefix, current, ct);

    public Task RenameAsync(DocTypeMeta meta, string oldName, string newName, bool merge, IReadOnlyList<DocTypeMeta> childTables, IReadOnlyList<ReferenceUpdate> references,
        CancellationToken ct = default, IReadOnlyList<ChildChanges>? sideWrites = null) =>
        meta.IsVirtual ? throw ReadOnly(meta) : inner.RenameAsync(meta, oldName, newName, merge, childTables, references, ct, sideWrites);
}
