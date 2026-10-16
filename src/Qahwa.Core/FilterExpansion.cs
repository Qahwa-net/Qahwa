using System.Globalization;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// The friendlier filter operators, turned into the basic ones before a query is built: <c>between</c> and <c>timespan</c> become a
/// lower and an upper bound, and the words <c>__user</c>, <c>__today</c> and <c>__now</c> stand for the person asking and the current
/// date or moment (so a saved filter such as "assigned to __user" works for everyone). All values still go through the normal
/// conversion for the field's type, and are bound as parameters later.
/// </summary>
public static class FilterExpansion
{
    public static readonly string[] Timespans =
    [
        "today", "yesterday", "tomorrow", "this week", "last week", "next week", "this month", "last month", "next month",
        "this quarter", "last quarter", "this year", "last year", "next year", "last 7 days", "last 30 days", "last 90 days", "next 7 days", "next 30 days",
    ];

    public static bool IsRangeType(FieldType type) =>
        type is FieldType.Date or FieldType.Datetime or FieldType.Int or FieldType.Float or FieldType.Decimal or FieldType.Currency or FieldType.Percent or FieldType.Rating or FieldType.Duration;

    /// <summary>Replaces <c>__user</c>, <c>__today</c> and <c>__now</c> (only exactly these words) in a value or a list of values.</summary>
    public static object? Dynamic(object? value, string? user, DateTime now)
    {
        object? One(object? v) => FieldValueConverter.Unwrap(v) switch
        {
            "__user" when user is not null => user,
            "__today" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "__now" => now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            _ => v,
        };

        var unwrapped = FieldValueConverter.Unwrap(value);
        return unwrapped is System.Collections.IEnumerable and not string ? FieldValueConverter.UnwrapList(value).Select(One).ToList() : One(value);
    }

    /// <summary>
    /// "between" and "timespan" as bounds. Returns the filter as it was for any other operator.
    /// </summary>
    /// <exception cref="FormatException">The operator cannot be used on this kind of field, or the value is not right.</exception>
    public static IReadOnlyList<RawFilter> Expand(RawFilter raw, FieldType type, DateTime now)
    {
        var op = raw.Op.Trim().ToLowerInvariant();
        if (op is not ("between" or "timespan"))
            return [raw];
        if (!IsRangeType(type))
            throw new FormatException($"'{op}' works on dates, moments and numbers.");

        var isMoment = type == FieldType.Datetime;
        if (op == "between")
        {
            var pair = FieldValueConverter.UnwrapList(raw.Value).ToList();
            if (pair.Count != 2 || pair.Any(p => FieldValueConverter.Unwrap(p) is null or ""))
                throw new FormatException("'between' needs two values: from and to.");
            // A moment filtered up to a date means "to the end of that day".
            var upperIsDay = isMoment && FieldValueConverter.Unwrap(pair[1]) is string s && s.Length == 10;
            return [new RawFilter(raw.Field, ">=", pair[0]), new RawFilter(raw.Field, upperIsDay ? "<" : "<=", upperIsDay ? Day(DateOnly.ParseExact((string)FieldValueConverter.Unwrap(pair[1])!, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(1), moment: true) : pair[1])];
        }

        var name = (FieldValueConverter.Unwrap(raw.Value) as string)?.Trim().ToLowerInvariant().Replace('_', ' ');
        if (type is not (FieldType.Date or FieldType.Datetime))
            throw new FormatException("'timespan' works on dates and moments.");
        var (from, to) = Span(name, DateOnly.FromDateTime(now)) ?? throw new FormatException($"Unknown timespan. Use one of: {string.Join(", ", Timespans)}.");
        return isMoment
            ? [new RawFilter(raw.Field, ">=", Day(from, moment: true)), new RawFilter(raw.Field, "<", Day(to.AddDays(1), moment: true))]
            : [new RawFilter(raw.Field, ">=", Day(from, moment: false)), new RawFilter(raw.Field, "<=", Day(to, moment: false))];
    }

    private static string Day(DateOnly day, bool moment) =>
        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + (moment ? "T00:00:00Z" : string.Empty);

    /// <summary>The first and last day of a named span relative to today (weeks start on Monday).</summary>
    public static (DateOnly From, DateOnly To)? Span(string? name, DateOnly today)
    {
        DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        DateOnly Month(DateOnly d) => new(d.Year, d.Month, 1);
        DateOnly Quarter(DateOnly d) => new(d.Year, ((d.Month - 1) / 3) * 3 + 1, 1);
        (DateOnly, DateOnly) Weeks(int offset) { var m = Monday(today).AddDays(7 * offset); return (m, m.AddDays(6)); }
        (DateOnly, DateOnly) Months(int offset) { var m = Month(today).AddMonths(offset); return (m, m.AddMonths(1).AddDays(-1)); }
        (DateOnly, DateOnly) Quarters(int offset) { var q = Quarter(today).AddMonths(3 * offset); return (q, q.AddMonths(3).AddDays(-1)); }
        (DateOnly, DateOnly) Years(int offset) { var y = new DateOnly(today.Year + offset, 1, 1); return (y, new DateOnly(y.Year, 12, 31)); }

        return name switch
        {
            "today" => (today, today),
            "yesterday" => (today.AddDays(-1), today.AddDays(-1)),
            "tomorrow" => (today.AddDays(1), today.AddDays(1)),
            "this week" => Weeks(0),
            "last week" => Weeks(-1),
            "next week" => Weeks(1),
            "this month" => Months(0),
            "last month" => Months(-1),
            "next month" => Months(1),
            "this quarter" => Quarters(0),
            "last quarter" => Quarters(-1),
            "this year" => Years(0),
            "last year" => Years(-1),
            "next year" => Years(1),
            "last 7 days" => (today.AddDays(-6), today),
            "last 30 days" => (today.AddDays(-29), today),
            "last 90 days" => (today.AddDays(-89), today),
            "next 7 days" => (today, today.AddDays(6)),
            "next 30 days" => (today, today.AddDays(29)),
            _ => null,
        };
    }
}
