namespace Qahwa.Core;

/// <summary>
/// Generates document names according to a DocType's autoname rule. A series pattern such as <c>INV-{YYYY}-.####</c> may hold
/// <c>{YYYY}</c>, <c>{YY}</c>, <c>{MM}</c>, <c>{DD}</c> (today, UTC) and <c>{fieldname}</c> (a field of the document) before the
/// <c>.####</c>; each different resulting prefix has its own counter (INV-2026-0001, then INV-2027-0001 next year).
/// </summary>
public sealed partial class NamingService(IDocumentRepository repository, TimeProvider? clock = null)
{
    public const int MaxFieldPartLength = 30;

    [System.Text.RegularExpressions.GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_]*)\}")]
    private static partial System.Text.RegularExpressions.Regex Token();

    public static readonly string[] DateTokens = ["YYYY", "YY", "MM", "DD"];

    /// <summary>The names of the fields a pattern reads (for checking it against a DocType).</summary>
    public static IEnumerable<string> FieldTokens(string pattern) =>
        Token().Matches(pattern).Select(m => m.Groups[1].Value).Where(t => !DateTokens.Contains(t, StringComparer.Ordinal));

    /// <summary>A regular expression that matches any prefix this pattern can produce (to tell which DocTypes a stored counter belongs to).</summary>
    public static string PrefixRegex(string prefixPattern) =>
        "^" + Token().Replace(System.Text.RegularExpressions.Regex.Escape(prefixPattern).Replace(@"\{", "{").Replace(@"\}", "}"), m =>
            m.Groups[1].Value switch { "YYYY" => @"\d{4}", "YY" or "MM" or "DD" => @"\d{2}", _ => @"[A-Za-z0-9_\-/]+" }) + "$";

    private string Resolve(string prefix, IReadOnlyDictionary<string, object?> values)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        return Token().Replace(prefix, m =>
        {
            var token = m.Groups[1].Value;
            switch (token)
            {
                case "YYYY": return now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture);
                case "YY": return now.ToString("yy", System.Globalization.CultureInfo.InvariantCulture);
                case "MM": return now.ToString("MM", System.Globalization.CultureInfo.InvariantCulture);
                case "DD": return now.ToString("dd", System.Globalization.CultureInfo.InvariantCulture);
            }

            var text = FieldValueConverter.Unwrap(values.GetValueOrDefault(token))?.ToString()?.Trim();
            if (string.IsNullOrEmpty(text))
                throw new DocumentValidationException([new FieldError(token, "A value is required to name this document.")]);
            // The value becomes part of a name (and of a counter's name): only letters, digits, - _ and / survive.
            var clean = new string(text.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/').ToArray());
            if (clean.Length == 0)
                throw new DocumentValidationException([new FieldError(token, "Use letters or digits here: this value is part of the document's name.")]);
            return clean.Length > MaxFieldPartLength ? clean[..MaxFieldPartLength] : clean;
        });
    }

    public async Task<string> NextNameAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> values,
        string? promptName,
        CancellationToken ct = default)
    {
        var rule = meta.Autoname;

        if (rule is null or "hash")
            return Guid.NewGuid().ToString("N")[..10];

        if (rule == "prompt")
            return CheckName(promptName, "name");

        if (rule.StartsWith("field:", StringComparison.Ordinal))
        {
            var field = rule["field:".Length..];
            return CheckName(values.GetValueOrDefault(field) as string, field);
        }

        var (pattern, digits) = ParseSeries(rule);
        var prefix = Resolve(pattern, values);
        var next = await repository.NextSeriesAsync(prefix, ct);
        return FormatSeries(prefix, digits, next);
    }

    public static (string Prefix, int Digits) ParseSeries(string pattern)
    {
        var index = pattern.LastIndexOf(".#", StringComparison.Ordinal);
        if (index < 0)
            throw new ArgumentException($"Not a series pattern: {pattern}", nameof(pattern));
        return (pattern[..index], pattern.Length - index - 1);
    }

    public static string FormatSeries(string prefix, int digits, long number) =>
        prefix + number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');

    private static string CheckName(string? name, string source)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new DocumentValidationException([new FieldError(source, "A value is required to name this document.")]);
        if (name.Length > FieldTypes.ShortTextLength)
            throw new DocumentValidationException([new FieldError(source, "Document name is too long (max 140 characters).")]);
        return name;
    }
}
