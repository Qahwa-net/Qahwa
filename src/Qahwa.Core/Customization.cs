using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>
/// Changes to one property of a field that the DocType's definition declared. Null means "leave as defined";
/// for text an empty string clears the value.
/// </summary>
public sealed record FieldOverride
{
    public string? Label { get; init; }
    public string? Description { get; init; }
    public bool? Hidden { get; init; }
    public bool? ReadOnly { get; init; }
    public bool? Required { get; init; }
    public string? Default { get; init; }
    public bool? InListView { get; init; }
    public int? PermLevel { get; init; }

    /// <summary>The choices of a Select field (newline-separated). Not for other types: link targets cannot change.</summary>
    public string? Options { get; init; }

    public string? DependsOn { get; init; }
    public string? MandatoryDependsOn { get; init; }
    public string? ReadOnlyDependsOn { get; init; }

    [JsonIgnore]
    public bool IsEmpty => this == new FieldOverride();
}

/// <summary>
/// What an administrator changed on a DocType that ships with the framework or an app (the equivalent of Frappe's
/// Customize Form: custom fields and property setters): new fields, changed properties of existing fields, their order,
/// and the title field. The definition itself is never edited, so a customisation can always be reset.
/// </summary>
public sealed record Customization
{
    public const int MaxCustomFields = 50;
    public const string CustomPrefix = "custom_";

    public IReadOnlyList<DocFieldMeta> CustomFields { get; init; } = [];

    public IReadOnlyDictionary<string, FieldOverride> Overrides { get; init; } = new Dictionary<string, FieldOverride>();

    /// <summary>The complete order of the fields, by name. Fields it does not list follow in their usual order.</summary>
    public IReadOnlyList<string> Order { get; init; } = [];

    /// <summary>The field that names a document in lists and links; empty restores the definition's.</summary>
    public string? TitleField { get; init; }

    [JsonIgnore]
    public bool IsEmpty => CustomFields.Count == 0 && Overrides.Count == 0 && Order.Count == 0 && TitleField is null;
}

public static class CustomizationApplier
{
    private static string? Text(string? change, string? current) => change is null ? current : change.Length == 0 ? null : change;

    private static DocFieldMeta Merge(DocFieldMeta field, FieldOverride o) => field with
    {
        Label = Text(o.Label, field.Label),
        Description = Text(o.Description, field.Description),
        Hidden = o.Hidden ?? field.Hidden,
        ReadOnly = o.ReadOnly ?? field.ReadOnly,
        Required = o.Required ?? field.Required,
        Default = Text(o.Default, field.Default),
        InListView = o.InListView ?? field.InListView,
        PermLevel = o.PermLevel ?? field.PermLevel,
        Options = field.Type == FieldType.Select ? Text(o.Options, field.Options) : field.Options,
        DependsOn = Text(o.DependsOn, field.DependsOn),
        MandatoryDependsOn = Text(o.MandatoryDependsOn, field.MandatoryDependsOn),
        ReadOnlyDependsOn = Text(o.ReadOnlyDependsOn, field.ReadOnlyDependsOn),
    };

    /// <summary>The DocType as it works once the customisation is applied.</summary>
    public static DocTypeMeta Apply(DocTypeMeta definition, Customization customization)
    {
        var fields = definition.Fields
            .Select(f => customization.Overrides.TryGetValue(f.Name, out var o) ? Merge(f, o) : f)
            .Concat(customization.CustomFields)
            .ToList();

        if (customization.Order.Count > 0)
        {
            var byName = fields.GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var listed = customization.Order.Where(byName.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            var rest = fields.Where(f => !listed.Contains(f.Name, StringComparer.Ordinal));
            fields = listed.Select(n => byName[n]).Concat(rest).ToList();
        }

        var title = customization.TitleField is null ? definition.TitleField : customization.TitleField.Length == 0 ? null : customization.TitleField;
        return definition with { Fields = fields, TitleField = title };
    }
}

public sealed record StoredCustomization(string DocType, string Definition, DateTime Modified, string ModifiedBy);

public interface ICustomizationStore
{
    /// <summary>Returns an empty list when the table has not been created yet.</summary>
    Task<IReadOnlyList<StoredCustomization>> ListAsync(CancellationToken ct = default);

    Task<StoredCustomization?> GetAsync(string docType, CancellationToken ct = default);

    /// <param name="expectedModified">Null when the DocType has no customisation yet.</param>
    /// <returns>False when someone else changed (or created) it since <paramref name="expectedModified"/>.</returns>
    Task<bool> SaveAsync(StoredCustomization row, DateTime? expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string docType, CancellationToken ct = default);
}

public sealed record CustomizationItem(string Name, string? Module, DocTypeSource Source, bool Customized, int CustomFieldCount);

public sealed record CustomizationView(
    string Name,
    DocTypeSource Source,
    bool Customized,
    DateTime? Modified,
    DocTypeMeta Definition,
    DocTypeMeta Effective,
    Customization Customization,
    bool CanEdit);

/// <summary>
/// The Customize Form tool: lets a System Manager add fields to, and adjust, DocTypes they cannot redesign (those shipped
/// by the framework or an app). Fields added here get real columns; nothing is ever dropped, so removing a custom field
/// hides it and keeps its data.
/// </summary>
public sealed partial class CustomizationManager(
    IMutableMetaRegistry registry,
    ICustomizationStore store,
    ISchemaManager schema,
    PermissionService permissions,
    TimeProvider clock)
{
    private readonly SemaphoreSlim _changes = new(1, 1);

    // The definition each customised DocType had before customisation, so it can be re-applied or restored.
    private readonly Dictionary<string, DocTypeMeta> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    [GeneratedRegex(@"^fields\[(\d+)\](.*)$")]
    private static partial Regex FieldPathRegex();

    private bool CanManage(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!CanManage(user))
            throw new PermissionDeniedException("Only a System Manager can customise DocTypes.");
    }

    private bool CanEdit(DocTypeSource source, IUserContext user) =>
        source == DocTypeSource.Core ? permissions.IsAdministrator(user) : CanManage(user);

    private DocTypeMeta DefinitionOf(string name)
    {
        lock (_gate)
            return _definitions.TryGetValue(name, out var meta) ? meta : registry.Get(name);
    }

    /// <summary>Only DocTypes with a definition of their own can be customised: not child tables, and not ones made in the designer (edit those there).</summary>
    private DocTypeMeta RequireCustomisable(string name, out DocTypeSource source)
    {
        if (!registry.TryGet(name, out var current) || current.IsChildTable)
            throw new NotFoundException($"DocType '{name}' was not found.");
        source = registry.SourceOf(name)!.Value;
        if (source == DocTypeSource.Custom)
            throw new ConflictException($"'{name}' was made in the DocType Designer; change it there.");
        return DefinitionOf(name);
    }

    public async Task<IReadOnlyList<CustomizationItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var stored = (await store.ListAsync(ct)).ToDictionary(r => r.DocType, StringComparer.OrdinalIgnoreCase);
        return registry.All
            .Where(m => !m.IsChildTable && registry.SourceOf(m.Name) != DocTypeSource.Custom)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var customization = stored.TryGetValue(m.Name, out var row) ? TryParse(row.Definition) : null;
                return new CustomizationItem(m.Name, m.Module, registry.SourceOf(m.Name)!.Value, row is not null, customization?.CustomFields.Count ?? 0);
            })
            .ToList();
    }

    public async Task<CustomizationView> GetAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var definition = RequireCustomisable(name, out var source);
        var stored = await store.GetAsync(name, ct);
        var customization = stored is null ? new Customization() : TryParse(stored.Definition) ?? new Customization();
        return new CustomizationView(name, source, stored is not null, stored?.Modified, definition, registry.Get(name), customization, CanEdit(source, user));
    }

    private static Customization? TryParse(string json)
    {
        try
        {
            return DocTypeLoader.ParseCustomization(json);
        }
        catch (DocTypeValidationException)
        {
            return null;
        }
    }

    public async Task<CustomizationView> SaveAsync(
        string name, Customization customization, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            var definition = RequireCustomisable(name, out var source);
            if (!CanEdit(source, user))
                throw new PermissionDeniedException("Only an Administrator can customise the framework's own DocTypes.");

            var effective = Validate(definition, customization);
            var stored = await store.GetAsync(name, ct);
            if (stored?.Modified != expectedModified)
                throw new ConflictException($"'{name}' was customised by someone else after you opened it. Reload and try again.");

            await schema.ApplyAsync(effective, ct); // new columns only; nothing is dropped
            var row = new StoredCustomization(name, DocTypeLoader.SerializeCustomization(customization), Now(), user.User);
            if (!await store.SaveAsync(row, expectedModified, ct))
                throw new ConflictException($"'{name}' was customised by someone else. Reload and try again.");

            lock (_gate)
                _definitions[name] = definition;
            registry.Upsert(effective, source);
            return await GetAsync(name, user, ct);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Throws with every problem keyed by field name; otherwise returns the DocType as customised.</summary>
    private DocTypeMeta Validate(DocTypeMeta definition, Customization customization)
    {
        var errors = new List<FieldError>();
        var standard = definition.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);

        if (customization.CustomFields.Count > Customization.MaxCustomFields)
            errors.Add(new FieldError("customFields", $"At most {Customization.MaxCustomFields} custom fields are allowed."));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in customization.CustomFields)
        {
            var at = $"fields[{field.Name}]";
            if (!field.Name.StartsWith(Customization.CustomPrefix, StringComparison.Ordinal))
                errors.Add(new FieldError($"{at}.name", $"A custom field's name must start with '{Customization.CustomPrefix}'."));
            if (standard.ContainsKey(field.Name) || !seen.Add(field.Name))
                errors.Add(new FieldError($"{at}.name", $"There is already a field named '{field.Name}'."));
            if (!FieldTypeCatalog.IsSupported(field.Type) || field.Type is FieldType.Table or FieldType.Password)
                errors.Add(new FieldError($"{at}.type", $"{field.Type} fields cannot be added to an existing DocType."));
            if (field.Formula is not null && field.FetchFrom is not null)
                errors.Add(new FieldError($"{at}.formula", "A field cannot be both calculated and fetched."));
        }

        foreach (var (field, change) in customization.Overrides)
        {
            var at = $"fields[{field}]";
            if (!standard.TryGetValue(field, out var original))
            {
                errors.Add(new FieldError(at, customization.CustomFields.Any(c => c.Name == field)
                    ? "Change a custom field by editing the field itself."
                    : $"'{field}' is not a field of this DocType."));
                continue;
            }

            if (change.Options is not null && original.Type != FieldType.Select)
                errors.Add(new FieldError($"{at}.options", "Only the choices of a Select field can be changed."));
            if (change.Required == true && !FieldTypes.HasColumn(original.Type))
                errors.Add(new FieldError($"{at}.required", "This kind of field cannot be required."));
            if (change.PermLevel is < 0 or > DocTypeLoader.MaxPermLevel)
                errors.Add(new FieldError($"{at}.permLevel", $"The permission level must be between 0 and {DocTypeLoader.MaxPermLevel}."));
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var effective = CustomizationApplier.Apply(definition, customization);
        var all = new HashSet<string>(effective.Fields.Select(f => f.Name), StringComparer.Ordinal);
        foreach (var name in customization.Order.Where(n => !all.Contains(n)))
            errors.Add(new FieldError("order", $"'{name}' is not a field of this DocType."));

        foreach (var problem in DocTypeLoader.Check(effective).Concat(registry.CheckReferences(effective)))
            errors.Add(NameTheField(effective, problem));

        if (errors.Count > 0)
            throw new DocumentValidationException(errors.DistinctBy(e => (e.Field, e.Message)).ToList());
        return effective;
    }

    /// <summary>Errors come as <c>fields[3].label</c>; the Customize Form refers to fields by name, so say <c>fields[custom_x].label</c>.</summary>
    private static FieldError NameTheField(DocTypeMeta effective, FieldError error)
    {
        var match = FieldPathRegex().Match(error.Field);
        return match.Success && int.TryParse(match.Groups[1].Value, out var index) && index < effective.Fields.Count
            ? error with { Field = $"fields[{effective.Fields[index].Name}]{match.Groups[2].Value}" }
            : error;
    }

    /// <summary>Goes back to the DocType as defined. Custom fields disappear from forms; their columns and data stay.</summary>
    public async Task<CustomizationView> ResetAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            var definition = RequireCustomisable(name, out var source);
            if (!CanEdit(source, user))
                throw new PermissionDeniedException("Only an Administrator can customise the framework's own DocTypes.");

            await store.DeleteAsync(name, ct);
            registry.Upsert(definition, source);
            lock (_gate)
                _definitions.Remove(name);
            return await GetAsync(name, user, ct);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Applies every stored customisation to the registry (at start-up). A broken one is skipped and reported.</summary>
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                if (!registry.TryGet(row.DocType, out _) || registry.SourceOf(row.DocType) == DocTypeSource.Custom)
                    throw new DocTypeValidationException("the DocType no longer exists, or is now designed in the DocType Designer.");

                var source = registry.SourceOf(row.DocType)!.Value;
                var definition = DefinitionOf(row.DocType);
                var effective = Validate(definition, DocTypeLoader.ParseCustomization(row.Definition));
                lock (_gate)
                    _definitions[row.DocType] = definition;
                registry.Upsert(effective, source);
            }
            catch (Exception ex) when (ex is DocTypeValidationException or DocumentValidationException)
            {
                var reason = ex is DocumentValidationException invalid ? string.Join(" ", invalid.Errors.Select(e => $"{e.Field}: {e.Message}")) : ex.Message;
                failures.Add($"The customisation of '{row.DocType}' was skipped: {reason}");
            }
        }

        return failures;
    }

    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc); // PostgreSQL keeps microseconds
    }
}
