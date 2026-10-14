using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;

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

    // Table names are "tab" + name; keep the total under the 63-char identifier limit.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9 _-]{0,59}$")]
    private static partial Regex DocTypeNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_\-/]*\.#{1,10}$")]
    private static partial Regex SeriesPatternRegex();

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

    public static DocTypeMeta LoadFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (DocTypeValidationException ex)
        {
            throw new DocTypeValidationException($"{path}: {ex.Message}");
        }
    }

    public static void Validate(DocTypeMeta meta)
    {
        if (string.IsNullOrWhiteSpace(meta.Name) || !DocTypeNameRegex().IsMatch(meta.Name))
            throw new DocTypeValidationException(
                $"Invalid DocType name '{meta.Name}' (letters, digits, space, underscore, hyphen; max 60 chars).");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in meta.Fields)
        {
            if (!FieldNameRegex().IsMatch(field.Name))
                throw new DocTypeValidationException(
                    $"{meta.Name}: invalid field name '{field.Name}' (use lowercase letters, digits, underscore).");
            if (StandardFields.IsReserved(field.Name))
                throw new DocTypeValidationException($"{meta.Name}: '{field.Name}' is a reserved field name.");
            if (!seen.Add(field.Name))
                throw new DocTypeValidationException($"{meta.Name}: duplicate field '{field.Name}'.");
            if (field.Type is FieldType.Link or FieldType.Table && string.IsNullOrWhiteSpace(field.Options))
                throw new DocTypeValidationException(
                    $"{meta.Name}.{field.Name}: {field.Type} fields need 'options' naming the target DocType.");
        }

        if (meta.TitleField is not null && !seen.Contains(meta.TitleField))
            throw new DocTypeValidationException($"{meta.Name}: titleField '{meta.TitleField}' is not a field.");

        ValidateAutoname(meta, seen);

        foreach (var perm in meta.Permissions)
        {
            if (string.IsNullOrWhiteSpace(perm.Role))
                throw new DocTypeValidationException($"{meta.Name}: permission rule without a role.");
        }
    }

    private static void ValidateAutoname(DocTypeMeta meta, HashSet<string> fields)
    {
        var rule = meta.Autoname;
        if (rule is null or "hash" or "prompt")
            return;

        if (rule.StartsWith("field:", StringComparison.Ordinal))
        {
            if (!fields.Contains(rule["field:".Length..]))
                throw new DocTypeValidationException($"{meta.Name}: autoname references unknown field in '{rule}'.");
            return;
        }

        if (!SeriesPatternRegex().IsMatch(rule))
            throw new DocTypeValidationException(
                $"{meta.Name}: invalid autoname '{rule}' (use hash, prompt, field:<name> or a series like CUST-.#####).");
    }
}
