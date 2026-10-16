using System.Globalization;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed partial class DocumentService
{
    /// <summary>The key of an aggregate's column in a grouped report: <c>count</c>, or <c>sum_total</c>.</summary>
    public static string AggregateKey(string function, string? field) => function == "count" ? "count" : $"{function}_{field}";

    /// <summary>
    /// A grouped summary with several numbers per group (for example how many, the total and the average). Like <see cref="ReportAsync"/>
    /// it needs the report right and is worked out from the rows the person may read, so it cannot show more than the list does.
    /// </summary>
    public async Task<(IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated)> GroupedAsync(
        string doctype, string groupBy, IReadOnlyList<(string Function, string? Field)> aggregates, ListRequest request, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Read);
        permissions.Require(meta, user, PermAction.Report);
        await EnsureModuleAsync(meta, user, ct);

        var columns = meta.ColumnTypes();
        if (!columns.TryGetValue(groupBy ?? string.Empty, out var groupType) || groupType == FieldType.Password)
            throw new DocumentValidationException([new FieldError("groupBy", "Group by a field of this DocType.")]);
        if (aggregates.Count is 0 or > 5)
            throw new DocumentValidationException([new FieldError("aggregates", "Choose between 1 and 5 numbers to show.")]);

        var fields = new List<string> { groupBy! };
        foreach (var (function, field) in aggregates)
        {
            if (!Aggregates.Contains(function))
                throw new DocumentValidationException([new FieldError("aggregates", $"Use one of: {string.Join(", ", Aggregates)}.")]);
            if (function == "count")
                continue;
            if (field is null || !columns.TryGetValue(field, out var type) || !Numeric.Contains(type))
                throw new DocumentValidationException([new FieldError("aggregates", "Choose a numeric field to summarise.")]);
            if (!fields.Contains(field))
                fields.Add(field);
        }

        var (rows, truncated) = await ReadAllAsync(doctype, request with { Fields = fields, OrderBy = null }, user, ct);
        string? KeyOf(Dictionary<string, object?> r) => r.GetValueOrDefault(groupBy!) switch
        {
            null => null,
            bool b => b ? "1" : "0",
            var g => Convert.ToString(g, CultureInfo.InvariantCulture),
        };

        var result = rows.GroupBy(KeyOf).OrderBy(g => g.Key, StringComparer.Ordinal).Select(group =>
        {
            var line = new Dictionary<string, object?>(StringComparer.Ordinal) { [groupBy!] = group.Key };
            foreach (var (function, field) in aggregates)
            {
                var numbers = function == "count" ? [] : group.Select(r => r.GetValueOrDefault(field!)).Where(v => v is not null)
                    .Select(v => Convert.ToDecimal(v, CultureInfo.InvariantCulture)).ToList();
                line[AggregateKey(function, field)] = function switch
                {
                    "count" => (decimal)group.Count(),
                    "sum" => numbers.Sum(),
                    "avg" => numbers.Count == 0 ? 0 : numbers.Average(),
                    "min" => numbers.Count == 0 ? 0 : numbers.Min(),
                    _ => numbers.Count == 0 ? 0 : numbers.Max(),
                };
            }

            return line;
        }).ToList();
        return (result, truncated);
    }
}
