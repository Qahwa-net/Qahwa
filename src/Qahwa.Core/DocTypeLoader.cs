using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed class DocTypeValidationException(string message, IReadOnlyList<FieldError>? errors = null) : Exception(message)
{
    /// <summary>Every problem found, keyed by path (e.g. <c>fields[2].name</c>); empty for load errors.</summary>
    public IReadOnlyList<FieldError> Errors { get; } = errors ?? [];
}

/// <summary>Loads and validates DocType JSON files (the source of truth for metadata).</summary>
public static partial class DocTypeLoader
{
    public const int MaxFields = 150;
    public const int MaxPermissions = 50;

    /// <summary>Names the framework's own tables use ("tab" + name), so a DocType may not take them.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) { "Series" };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
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

    public static string Serialize(DocTypeMeta meta) => JsonSerializer.Serialize(meta, Options);

    public static DocTypeMeta LoadFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (DocTypeValidationException ex)
        {
            throw new DocTypeValidationException($"{path}: {ex.Message}", ex.Errors);
        }
    }

    /// <exception cref="DocTypeValidationException">The first problem found (see <see cref="Check"/> for all of them).</exception>
    public static void Validate(DocTypeMeta meta)
    {
        var errors = Check(meta);
        if (errors.Count > 0)
            throw new DocTypeValidationException($"{meta.Name}: {errors[0].Message}", errors);
    }

    /// <summary>Returns every problem with a DocType definition, each keyed by the path of the offending part.</summary>
    public static IReadOnlyList<FieldError> Check(DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        void Add(string path, string message) => errors.Add(new FieldError(path, message));

        if (string.IsNullOrWhiteSpace(meta.Name) || !DocTypeNameRegex().IsMatch(meta.Name))
            Add("name", $"Invalid DocType name '{meta.Name}' (letters, digits, space, underscore, hyphen; max 60 characters).");
        else if (ReservedNames.Contains(meta.Name))
            Add("name", $"'{meta.Name}' is reserved for the framework.");

        if (meta.Module is { Length: > 60 } || meta.Module?.Any(char.IsControl) == true)
            Add("module", "Module must be at most 60 characters.");

        if (meta.Fields.Count > MaxFields)
            Add("fields", $"A DocType can have at most {MaxFields} fields.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < meta.Fields.Count; i++)
            CheckField(meta.Fields[i], $"fields[{i}]", seen, Add);

        if (meta.TitleField is not null && !seen.Contains(meta.TitleField))
            Add("titleField", $"Title field '{meta.TitleField}' is not a field of this DocType.");

        CheckAutoname(meta, seen, Add);

        if (meta.Permissions.Count > MaxPermissions)
            Add("permissions", $"At most {MaxPermissions} permission rules are allowed.");
        for (var i = 0; i < meta.Permissions.Count; i++)
        {
            var role = meta.Permissions[i].Role;
            if (string.IsNullOrWhiteSpace(role))
                Add($"permissions[{i}].role", "Permission rule without a role.");
            else if (role.Length > FieldTypes.ShortTextLength)
                Add($"permissions[{i}].role", "Role name is too long.");
        }

        return errors;
    }

    private static void CheckField(DocFieldMeta field, string path, HashSet<string> seen, Action<string, string> add)
    {
        if (!FieldNameRegex().IsMatch(field.Name))
            add($"{path}.name", $"Invalid field name '{field.Name}' (use lowercase letters, digits and underscores, starting with a letter).");
        else if (StandardFields.IsReserved(field.Name))
            add($"{path}.name", $"'{field.Name}' is a reserved field name.");
        else if (!seen.Add(field.Name))
            add($"{path}.name", $"Duplicate field name '{field.Name}'.");

        if (field.Type is FieldType.Link or FieldType.Table && string.IsNullOrWhiteSpace(field.Options))
            add($"{path}.options", $"{field.Type} fields need a target DocType.");

        if (field.Type == FieldType.Select)
        {
            var options = field.SelectOptions().ToList();
            if (options.Count == 0)
                add($"{path}.options", "Select fields need at least one option.");
            else if (options.Count != options.Distinct(StringComparer.Ordinal).Count())
                add($"{path}.options", "Select options must be unique.");
            else if (!string.IsNullOrEmpty(field.Default) && !options.Contains(field.Default, StringComparer.Ordinal))
                add($"{path}.default", "The default must be one of the options.");
        }

        if (field.Label is { Length: > 140 })
            add($"{path}.label", "Label must be at most 140 characters.");
        if (field.Description is { Length: > 500 })
            add($"{path}.description", "Description must be at most 500 characters.");
        if (field.Options is { Length: > 4000 })
            add($"{path}.options", "Options are too long.");
        if (field.Default is { Length: > 140 })
            add($"{path}.default", "Default must be at most 140 characters.");
    }

    private static void CheckAutoname(DocTypeMeta meta, HashSet<string> fields, Action<string, string> add)
    {
        var rule = meta.Autoname;
        if (rule is null or "hash" or "prompt")
            return;

        if (rule.StartsWith("field:", StringComparison.Ordinal))
        {
            if (!fields.Contains(rule["field:".Length..]))
                add("autoname", $"Naming rule '{rule}' refers to a field that does not exist.");
            return;
        }

        if (!SeriesPatternRegex().IsMatch(rule))
            add("autoname", $"Invalid naming rule '{rule}' (use hash, prompt, field:<name> or a series like CUST-.#####).");
    }
}
