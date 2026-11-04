namespace Qahwa.Core;

/// <summary>
/// DocType Layouts (ADR-157): a simpler form for some people. A layout names a DocType, the roles it is for and the fields to hide from
/// them; the first enabled layout (by position) that fits a person is applied to the form they are sent. It is about what is shown, never
/// about what is allowed: permissions, validation and the API are unchanged, which is why a required field cannot be hidden.
/// </summary>
public static class DocTypeLayoutService
{
    public const string DocTypeName = "DocType Layout";

    public static IReadOnlyList<string> Lines(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>The rules a layout must meet: a real DocType, and hidden fields that exist and are not required.</summary>
    public static void Validate(IMetaRegistry registry, IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        if (values.GetValueOrDefault("document_type") is not string doctype)
            return;
        if (!registry.TryGet(doctype, out var meta))
        {
            errors.Add(new FieldError("document_type", $"There is no DocType named '{doctype}'."));
            return;
        }

        if (meta.IsChildTable)
            errors.Add(new FieldError("document_type", "A layout is for a DocType with documents of its own, not a child table."));
        foreach (var name in Lines(values.GetValueOrDefault("hidden_fields") as string))
        {
            var field = meta.Fields.FirstOrDefault(f => f.Name == name);
            if (field is null)
                errors.Add(new FieldError("hidden_fields", $"{doctype} has no field named '{name}'."));
            else if (field.Required)
                errors.Add(new FieldError("hidden_fields", $"'{name}' is required, so it cannot be hidden."));
        }
    }

    /// <summary>The layout rows that fit a person, first choice first.</summary>
    public static IEnumerable<Dictionary<string, object?>> Fitting(IEnumerable<Dictionary<string, object?>> layouts, IUserContext user) =>
        layouts
            .Where(l => l.GetValueOrDefault("enabled") is not false)
            .OrderBy(l => Convert.ToInt32(l.GetValueOrDefault("sequence") ?? 0, System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(l => l["name"] as string, StringComparer.Ordinal)
            .Where(l => Lines(l.GetValueOrDefault("roles") as string) is { Count: > 0 } roles ? roles.Any(r => user.Roles.Contains(r, StringComparer.Ordinal)) : true);

    /// <summary>The meta with the first fitting layout's fields hidden (required fields are never hidden, whatever the layout says).</summary>
    public static DocTypeMeta Apply(DocTypeMeta meta, IEnumerable<Dictionary<string, object?>> layouts, IUserContext user)
    {
        var chosen = Fitting(layouts, user).FirstOrDefault();
        if (chosen is null)
            return meta;
        var hide = Lines(chosen.GetValueOrDefault("hidden_fields") as string).ToHashSet(StringComparer.Ordinal);
        return hide.Count == 0 ? meta : meta with { Fields = meta.Fields.Select(f => hide.Contains(f.Name) && !f.Required ? f with { Hidden = true } : f).ToList() };
    }
}
