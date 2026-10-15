using System.Globalization;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record SeriesPoint(string? Label, decimal Value);

/// <param name="Truncated">More documents or more groups existed than were used.</param>
public sealed record Series(IReadOnlyList<SeriesPoint> Points, bool Truncated);

/// <summary>Numbers and charts over documents: one aggregate, or an aggregate per group (optionally per day, month or year).</summary>
public sealed partial class DocumentService
{
    public const int MaxSeriesPoints = 30;
    public static readonly string[] Periods = ["day", "month", "year"];

    private (DocTypeMeta Meta, IReadOnlyDictionary<string, FieldType> Columns, string Function) PrepareAnalytics(string doctype, string function, string? valueField, IUserContext user)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Read);
        permissions.Require(meta, user, PermAction.Report);

        function = (function ?? string.Empty).Trim().ToLowerInvariant();
        if (!Aggregates.Contains(function))
            throw new DocumentValidationException([new FieldError("function", $"Use one of: {string.Join(", ", Aggregates)}.")]);

        var columns = meta.ColumnTypes();
        if (function != "count" && (valueField is null || !columns.TryGetValue(valueField, out var valueType) || !Numeric.Contains(valueType)))
            throw new DocumentValidationException([new FieldError("valueField", "Choose a numeric field to summarise.")]);
        return (meta, columns, function);
    }

    private static decimal Apply(string function, IReadOnlyList<decimal> numbers, int count) => function switch
    {
        "count" => count,
        "sum" => numbers.Sum(),
        "avg" => numbers.Count == 0 ? 0 : numbers.Average(),
        "min" => numbers.Count == 0 ? 0 : numbers.Min(),
        _ => numbers.Count == 0 ? 0 : numbers.Max(),
    };

    private static List<decimal> Numbers(IEnumerable<Dictionary<string, object?>> rows, string? field) =>
        field is null ? [] : rows.Select(r => r.GetValueOrDefault(field)).Where(v => v is not null)
            .Select(v => Convert.ToDecimal(v, CultureInfo.InvariantCulture)).ToList();

    /// <summary>One number (a count, or the sum, average, smallest or largest of a numeric field) over the documents the person may read. Needs the report right.</summary>
    public async Task<(decimal Value, bool Truncated)> AggregateAsync(
        string doctype, string function, string? valueField, ListRequest request, IUserContext user, CancellationToken ct = default)
    {
        var (meta, _, fn) = PrepareAnalytics(doctype, function, valueField, user);
        await EnsureModuleAsync(meta, user, ct);
        var (rows, truncated) = await ReadAllAsync(doctype, request with { Fields = fn == "count" ? ["name"] : [valueField!] }, user, ct);
        return (Apply(fn, Numbers(rows, fn == "count" ? null : valueField), rows.Count), truncated);
    }

    private static string? SeriesKey(object? value, string? period) => value switch
    {
        null => null,
        DateOnly d => PeriodKey(d, period),
        DateTime dt => PeriodKey(DateOnly.FromDateTime(dt), period),
        bool b => b ? "1" : "0",
        var other => Convert.ToString(other, CultureInfo.InvariantCulture),
    };

    private static string PeriodKey(DateOnly date, string? period) => period switch
    {
        "year" => date.ToString("yyyy", CultureInfo.InvariantCulture),
        "month" => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// An aggregate per value of a field, for charts. A Date or Datetime field can be grouped by day, month or year and is
    /// then in date order; other fields list the biggest groups first, at most <see cref="MaxSeriesPoints"/>. Needs the report right.
    /// </summary>
    public async Task<Series> SeriesAsync(
        string doctype, string groupBy, string? period, string function, string? valueField, ListRequest request, IUserContext user, CancellationToken ct = default)
    {
        var (meta, columns, fn) = PrepareAnalytics(doctype, function, valueField, user);
        await EnsureModuleAsync(meta, user, ct);
        if (!columns.TryGetValue(groupBy ?? string.Empty, out var groupType) || groupType == FieldType.Password)
            throw new DocumentValidationException([new FieldError("groupBy", "Group by a field of this DocType.")]);

        var isDate = groupType is FieldType.Date or FieldType.Datetime;
        period = string.IsNullOrWhiteSpace(period) ? null : period.Trim().ToLowerInvariant();
        if (period is not null && (!isDate || !Periods.Contains(period)))
            throw new DocumentValidationException([new FieldError("period", isDate ? $"Use one of: {string.Join(", ", Periods)}." : "Only a date field can be grouped by a period.")]);

        var fields = new List<string> { groupBy! };
        if (fn != "count")
            fields.Add(valueField!);
        var (rows, truncated) = await ReadAllAsync(doctype, request with { Fields = fields }, user, ct);

        var points = rows.GroupBy(r => SeriesKey(r.GetValueOrDefault(groupBy!), isDate ? period ?? "day" : null))
            .Select(g => new SeriesPoint(g.Key, Apply(fn, Numbers(g, fn == "count" ? null : valueField), g.Count())))
            .ToList();
        points = isDate
            ? points.OrderBy(p => p.Label, StringComparer.Ordinal).ToList()
            : points.OrderByDescending(p => p.Value).ThenBy(p => p.Label, StringComparer.Ordinal).ToList();
        if (points.Count > MaxSeriesPoints)
        {
            // Dates keep the latest points, categories the biggest.
            points = isDate ? points.TakeLast(MaxSeriesPoints).ToList() : points.Take(MaxSeriesPoints).ToList();
            truncated = true;
        }

        return new Series(points, truncated);
    }
}
