using System.Collections;
using System.Globalization;
using System.Net.Mail;
using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Converts loosely typed input (JSON, strings, numbers) into the CLR type the database layer stores,
/// and converts values read back from the database into API-friendly types.
/// </summary>
public static class FieldValueConverter
{
    private const decimal MaxDecimal = 1_000_000_000_000m; // numeric(21,9) holds 12 integer digits

    public static object? Unwrap(object? raw)
    {
        if (raw is not JsonElement e)
            return raw;

        return e.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.TryGetDecimal(out var d) ? d : e.GetDouble(),
            _ => e.GetRawText(),
        };
    }

    /// <summary>Flattens a JSON array or any non-string enumerable into a list; scalars become one item.</summary>
    public static IReadOnlyList<object?> UnwrapList(object? raw)
    {
        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
            return array.EnumerateArray().Select(x => Unwrap(x)).ToList();
        if (raw is IEnumerable items and not string)
            return items.Cast<object?>().Select(Unwrap).ToList();
        return [Unwrap(raw)];
    }

    /// <exception cref="FormatException">The value cannot be represented as <paramref name="type"/>.</exception>
    public static object? ToStorage(FieldType type, object? raw)
    {
        try
        {
            return Convert(type, Unwrap(raw));
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or ArgumentException)
        {
            throw new FormatException($"Value is not a valid {type}.", ex);
        }
    }

    public static object? FromStorage(FieldType type, object? value) => value switch
    {
        null or DBNull => null,
        DateTime dt when type == FieldType.Date => DateOnly.FromDateTime(dt),
        TimeSpan ts when type == FieldType.Time => TimeOnly.FromTimeSpan(ts),
        // numeric(21,9) comes back as 2500.750000000; dividing by 1.000...0 strips the trailing zeros.
        decimal d => d / 1.0000000000000000000000000000m,
        DateTime { Kind: DateTimeKind.Unspecified } dt when type == FieldType.Datetime =>
            DateTime.SpecifyKind(dt, DateTimeKind.Utc),
        _ => value,
    };

    private static object? Convert(FieldType type, object? raw)
    {
        if (raw is null)
            return null;
        if (raw is string { Length: 0 } && !FieldTypes.IsTextual(type))
            return null;

        var inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case FieldType.Int:
            {
                var d = ToDecimal(raw, inv);
                if (d != decimal.Truncate(d))
                    throw new FormatException("Value must be a whole number.");
                return checked((int)d);
            }
            case FieldType.Float:
            {
                if (raw is bool)
                    throw new FormatException("Value must be a number.");
                var d = System.Convert.ToDouble(raw, inv);
                if (double.IsNaN(d) || double.IsInfinity(d))
                    throw new FormatException("Value must be a finite number.");
                return d;
            }
            case FieldType.Decimal or FieldType.Currency or FieldType.Percent:
            {
                var d = ToDecimal(raw, inv);
                if (Math.Abs(d) >= MaxDecimal)
                    throw new FormatException("Value is too large.");
                return d;
            }
            case FieldType.Check:
                return ToBool(raw);
            case FieldType.Date:
                return ToDate(raw, inv);
            case FieldType.Datetime:
                return ToDatetime(raw, inv);
            case FieldType.Time:
                return raw switch
                {
                    TimeOnly t => t,
                    TimeSpan ts => TimeOnly.FromTimeSpan(ts),
                    string s => TimeOnly.Parse(s, inv),
                    _ => throw new FormatException("Value must be a time."),
                };
            case FieldType.Password:
                throw new FormatException("Password fields are not supported yet.");
            case FieldType.Table or FieldType.SectionBreak or FieldType.ColumnBreak or FieldType.TabBreak:
                throw new FormatException($"{type} fields do not hold a value.");
        }

        var text = raw as string ?? System.Convert.ToString(raw, inv) ?? string.Empty;
        switch (type)
        {
            case FieldType.Email when !MailAddress.TryCreate(text, out _):
                throw new FormatException("Value must be a valid email address.");
            case FieldType.Url when !Uri.TryCreate(text, UriKind.Absolute, out _):
                throw new FormatException("Value must be an absolute URL.");
            case FieldType.Json:
                using (JsonDocument.Parse(text)) { }
                break;
        }

        return text;
    }

    private static decimal ToDecimal(object raw, IFormatProvider inv) =>
        raw is bool ? throw new FormatException("Value must be a number.") : System.Convert.ToDecimal(raw, inv);

    private static bool ToBool(object raw) => raw switch
    {
        bool b => b,
        long l when l is 0 or 1 => l == 1,
        int i when i is 0 or 1 => i == 1,
        decimal d when d is 0 or 1 => d == 1,
        string s when s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1" => true,
        string s when s.Equals("false", StringComparison.OrdinalIgnoreCase) || s == "0" => false,
        _ => throw new FormatException("Value must be true or false."),
    };

    private static DateOnly ToDate(object raw, IFormatProvider inv) => raw switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        DateTimeOffset dto => DateOnly.FromDateTime(dto.UtcDateTime),
        string s when DateOnly.TryParse(s, inv, DateTimeStyles.None, out var d) => d,
        string s when DateTime.TryParse(s, inv, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt) =>
            DateOnly.FromDateTime(dt),
        _ => throw new FormatException("Value must be a date (yyyy-MM-dd)."),
    };

    private static DateTime ToDatetime(object raw, IFormatProvider inv)
    {
        var utc = raw switch
        {
            DateTime dt => dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime(),
            DateTimeOffset dto => dto.UtcDateTime,
            string s => DateTimeOffset.Parse(s, inv, DateTimeStyles.AssumeUniversal).UtcDateTime,
            _ => throw new FormatException("Value must be a date and time."),
        };

        // PostgreSQL stores microseconds; truncate so values round-trip exactly.
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
