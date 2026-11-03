using System.Collections;
using System.Globalization;

namespace Qahwa.Abstractions;

/// <summary>
/// The base of the classes the Qahwa source generator writes from DocType files: a typed view over the values of one document.
/// The values stay a dictionary (what the engine reads and writes), so <see cref="Values"/> goes straight into
/// <c>InsertAsync</c> and <c>UpdateAsync</c> and a document from <c>GetAsync</c> can be wrapped without copying.
/// </summary>
public abstract class TypedDocument
{
    protected TypedDocument(Dictionary<string, object?> values) => Values = values ?? throw new ArgumentNullException(nameof(values));

    /// <summary>The values of the document, keyed by field name.</summary>
    public Dictionary<string, object?> Values { get; }

    /// <summary>The name (the id) of the document; empty until it is saved.</summary>
    public string? Name
    {
        get => Get<string>("name");
        set => Set("name", value);
    }

    /// <summary>0 draft, 1 submitted, 2 cancelled.</summary>
    public int? Docstatus => Get<int?>("docstatus");

    public DateTime? Creation => Get<DateTime?>("creation");

    public DateTime? Modified => Get<DateTime?>("modified");

    public string? Owner => Get<string>("owner");

    /// <summary>Reads a field, converting what the engine (or a JSON body) holds to the type asked for. A missing field is the default value.</summary>
    protected T? Get<T>(string field)
    {
        if (!Values.TryGetValue(field, out var raw) || raw is null)
            return default;
        return (T?)ConvertTo(raw, typeof(T));
    }

    protected void Set(string field, object? value) => Values[field] = value;

    /// <summary>The rows of a Table field as typed rows. The rows are views over the same dictionaries, so changing a row changes the document.</summary>
    protected List<TRow> Rows<TRow>(string field, Func<Dictionary<string, object?>, TRow> wrap)
    {
        var result = new List<TRow>();
        if (!Values.TryGetValue(field, out var raw) || raw is not IEnumerable rows || raw is string)
            return result;
        foreach (var row in rows)
            if (row is Dictionary<string, object?> dictionary)
                result.Add(wrap(dictionary));
        return result;
    }

    /// <summary>Adds an empty row to a Table field and returns it.</summary>
    protected TRow AddRow<TRow>(string field, Func<Dictionary<string, object?>, TRow> wrap)
    {
        var row = new Dictionary<string, object?>();
        if (Values.TryGetValue(field, out var raw) && raw is List<Dictionary<string, object?>> typed)
            typed.Add(row);
        else
        {
            var list = new List<Dictionary<string, object?>>();
            if (raw is IEnumerable existing && raw is not string)
                foreach (var item in existing)
                    if (item is Dictionary<string, object?> d)
                        list.Add(d);
            list.Add(row);
            Values[field] = list;
        }

        return wrap(row);
    }

    private static object? ConvertTo(object raw, Type wanted)
    {
        var target = Nullable.GetUnderlyingType(wanted) ?? wanted;
        if (target.IsInstanceOfType(raw))
            return raw;
        var inv = CultureInfo.InvariantCulture;
        if (target == typeof(string))
            return Convert.ToString(raw, inv);
        if (target == typeof(DateOnly))
            return raw switch
            {
                DateTime dt => DateOnly.FromDateTime(dt),
                DateTimeOffset dto => DateOnly.FromDateTime(dto.DateTime),
                string s => DateOnly.Parse(s, inv),
                _ => throw new InvalidCastException($"A {raw.GetType().Name} is not a date."),
            };
        if (target == typeof(TimeOnly))
            return raw switch
            {
                TimeSpan ts => TimeOnly.FromTimeSpan(ts),
                DateTime dt => TimeOnly.FromDateTime(dt),
                string s => TimeOnly.Parse(s, inv),
                _ => throw new InvalidCastException($"A {raw.GetType().Name} is not a time."),
            };
        if (target == typeof(DateTime))
            return raw switch
            {
                DateTimeOffset dto => dto.UtcDateTime,
                DateOnly d => d.ToDateTime(TimeOnly.MinValue),
                string s => DateTime.Parse(s, inv, DateTimeStyles.RoundtripKind),
                _ => throw new InvalidCastException($"A {raw.GetType().Name} is not a date and time."),
            };
        if (target == typeof(bool))
            return raw is string text ? text is "1" or "true" or "True" : Convert.ToBoolean(raw, inv);
        return Convert.ChangeType(raw, target, inv);
    }
}
