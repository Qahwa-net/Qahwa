namespace Qahwa.Core;

/// <summary>Generates document names according to a DocType's autoname rule.</summary>
public sealed class NamingService(IDocumentRepository repository)
{
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

        var (prefix, digits) = ParseSeries(rule);
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
