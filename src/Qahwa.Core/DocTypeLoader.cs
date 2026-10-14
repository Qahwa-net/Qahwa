using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qahwa.Core;

public sealed class DocTypeValidationException(string message) : Exception(message);

/// <summary>Loads and validates DocType JSON files (the source of truth for metadata).</summary>
public static partial class DocTypeLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // Identifiers end up in SQL, so they are restricted to a safe set.
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex FieldNameRegex();

    public static DocTypeMeta Parse(string json)
    {
        DocTypeMeta? meta;
        try
        {
            meta = JsonSerializer.Deserialize<DocTypeMeta>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid DocType JSON: {ex.Message}");
        }

        if (meta is null)
            throw new DocTypeValidationException("DocType JSON is empty.");

        Validate(meta);
        return meta;
    }

    public static DocTypeMeta LoadFile(string path) => Parse(File.ReadAllText(path));

    public static void Validate(DocTypeMeta meta)
    {
        if (string.IsNullOrWhiteSpace(meta.Name))
            throw new DocTypeValidationException("DocType name is required.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in meta.Fields)
        {
            if (!FieldNameRegex().IsMatch(field.Name))
                throw new DocTypeValidationException(
                    $"{meta.Name}: invalid field name '{field.Name}' (use lowercase letters, digits, underscore).");
            if (!seen.Add(field.Name))
                throw new DocTypeValidationException($"{meta.Name}: duplicate field '{field.Name}'.");
        }

        if (meta.TitleField is not null && !seen.Contains(meta.TitleField))
            throw new DocTypeValidationException($"{meta.Name}: titleField '{meta.TitleField}' is not a field.");
    }
}
