using Qahwa.Abstractions;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>One row of a child table while a document is being saved.</summary>
internal sealed class RowInput(string? name, Dictionary<string, object?> values, Dictionary<string, object?>? existing = null)
{
    /// <summary>The row's id when the client refers to a row that is already saved.</summary>
    public string? Name { get; } = name;

    /// <summary>The values the client sent, plus anything the engine computed.</summary>
    public Dictionary<string, object?> Values { get; set; } = values;

    /// <summary>What is saved for this row now (null for a new row).</summary>
    public Dictionary<string, object?>? Existing { get; set; } = existing;

    /// <summary>The row as it will be after the save: saved values overlaid with the new ones.</summary>
    public Dictionary<string, object?> Merged()
    {
        var merged = Existing is null ? new Dictionary<string, object?>(StringComparer.Ordinal) : new Dictionary<string, object?>(Existing, StringComparer.Ordinal);
        foreach (var (key, value) in Values)
            merged[key] = value;
        return merged;
    }
}

/// <summary>Reads a document for <c>lookup()</c> exactly as the person could read it themselves (all permission layers), or null when they cannot.</summary>
internal delegate Task<Dictionary<string, object?>?> LinkReader(DocTypeMeta target, string name, IUserContext user, CancellationToken ct);

/// <summary>One child table of a document: the field, its DocType and the rows being saved.</summary>
internal sealed record TableInput(DocFieldMeta Field, DocTypeMeta Child, List<RowInput> Rows, bool Provided = true);

/// <summary>
/// Field logic that needs no code: default expressions, "fetch from", calculated fields, show/require conditions and
/// validation rules. It runs the same expressions the Desk runs while a form is edited, so what the user sees while
/// typing is what the server enforces on save.
/// </summary>
internal sealed class DocumentLogic(
    IMetaRegistry registry,
    IDocumentRepository repository,
    PermissionService permissions,
    TimeProvider clock)
{
    private const int MaxPasses = 3;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DocTypeMeta, string[]> LookupCache = new();

    /// <summary>The Link fields this DocType's expressions read through <c>lookup()</c> (none for most DocTypes).</summary>
    internal static string[] LookupLinks(DocTypeMeta meta) => LookupCache.GetValue(meta, m =>
    {
        var sources = new List<string>();
        foreach (var f in m.Fields)
        {
            if (f.Formula is not null) sources.Add(f.Formula);
            if (f.Default is { Length: > 1 } d && d.StartsWith('=')) sources.Add(d[1..]);
            if (f.DependsOn is not null) sources.Add(f.DependsOn);
            if (f.MandatoryDependsOn is not null) sources.Add(f.MandatoryDependsOn);
            if (f.ReadOnlyDependsOn is not null) sources.Add(f.ReadOnlyDependsOn);
        }

        sources.AddRange(m.Validations.Select(v => v.Expression));
        var links = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            try
            {
                foreach (var (link, _) in Expression.Parse(source).Lookups)
                    links.Add(link);
            }
            catch (ExpressionException)
            {
                // a broken expression is reported when the DocType is saved
            }
        }

        return links.ToArray();
    });

    /// <summary>
    /// The documents the Link fields of <paramref name="record"/> point at, for <c>lookup()</c>. Read through <paramref name="read"/>, so
    /// every permission layer applies and a document the person cannot open (or a field they cannot see) is simply not there.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>?> LinkedAsync(
        DocTypeMeta meta, IReadOnlyDictionary<string, object?> record, IUserContext user, LinkReader read, CancellationToken ct)
    {
        var links = LookupLinks(meta);
        if (links.Length == 0)
            return null;

        var result = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var name in links)
        {
            var field = meta.Fields.FirstOrDefault(f => f.Name == name && f.Type == FieldType.Link);
            if (field?.Options is null || record.GetValueOrDefault(name) is not string { Length: > 0 } value || !registry.TryGet(field.Options, out var target))
                continue;
            if (await read(target, value, user, ct) is not { } doc)
                continue;
            result[name] = doc.Where(kv => target.Fields.FirstOrDefault(f => f.Name == kv.Key)?.Type != FieldType.Password)
                .ToDictionary(kv => kv.Key, kv => Values.FromStorage(kv.Value), StringComparer.Ordinal);
        }

        return result;
    }

    public ExpressionContext Context(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<TableInput> tables,
        IReadOnlyDictionary<string, object?>? parent,
        IUserContext user,
        bool isNew,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? linked = null)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
            fields[key] = Values.FromStorage(value);

        foreach (var table in tables)
        {
            fields[table.Field.Name] = table.Rows
                .Select(row => (object?)Normalize(row.Merged()))
                .ToList();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        return new ExpressionContext
        {
            Fields = fields,
            Parent = parent is null ? null : Normalize(parent),
            User = user.User,
            Roles = user.Roles,
            IsNew = isNew,
            Linked = linked,
            Today = now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            Now = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    private static Dictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?> record) =>
        record.ToDictionary(kv => kv.Key, kv => Values.FromStorage(kv.Value), StringComparer.Ordinal);

    public static bool IsActive(DocFieldMeta field, ExpressionContext? context) =>
        field.DependsOn is null || context is null || Expression.Parse(field.DependsOn).IsTrue(context);

    /// <summary>Required by the field itself or by its condition, and only while the field is shown. Calculated fields are never required.</summary>
    public static bool IsRequired(DocFieldMeta field, ExpressionContext? context)
    {
        if (field.Formula is not null || !IsActive(field, context))
            return false;

        return field.Required
               || (field.MandatoryDependsOn is not null && context is not null && Expression.Parse(field.MandatoryDependsOn).IsTrue(context));
    }

    /// <summary>
    /// Fills in expression defaults, copies "fetch from" values and calculates formulas for a document and its rows.
    /// <paramref name="changed"/> lists the document fields the user changed; fields the engine sets are added to it.
    /// </summary>
    public async Task RunAsync(
        DocTypeMeta meta,
        Dictionary<string, object?> values,
        HashSet<string> changed,
        List<TableInput> tables,
        IUserContext user,
        bool isNew,
        List<FieldError> errors,
        CancellationToken ct,
        LinkReader? read = null)
    {
        if (!meta.HasLogic() && tables.All(t => !t.Child.HasLogic()))
            return;

        // What lookup() can see: the linked documents of the record and of each row (empty unless an expression uses lookup).
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? docLinked = null;
        var rowLinked = new Dictionary<RowInput, IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>?>();
        async Task ResolveAsync()
        {
            if (read is null)
                return;
            docLinked = await LinkedAsync(meta, values, user, read, ct);
            foreach (var table in tables)
                foreach (var row in table.Rows)
                    rowLinked[row] = await LinkedAsync(table.Child, row.Merged(), user, read, ct);
        }

        await ResolveAsync();
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? RowLinks(RowInput row) => rowLinked.GetValueOrDefault(row);

        if (isNew)
        {
            ApplyExpressionDefaults(meta, values, changed, Context(meta, values, tables, null, user, isNew, docLinked), errors, string.Empty);
            foreach (var table in tables)
            {
                foreach (var row in table.Rows.Where(r => r.Existing is null))
                {
                    var rowContext = Context(table.Child, row.Merged(), [], values, user, isNew, RowLinks(row));
                    ApplyExpressionDefaults(table.Child, row.Values, [], rowContext, errors, $"{table.Field.Name}[{table.Rows.IndexOf(row)}].");
                }
            }
        }

        await ApplyFetchesAsync(meta, values, changed, isNew, user, errors, string.Empty, ct);
        foreach (var table in tables)
        {
            foreach (var row in table.Rows)
            {
                // For a saved row only the values that differ from what is saved count as changed.
                var rowChanged = row.Values
                    .Where(kv => row.Existing is null || !Equals(row.Existing.GetValueOrDefault(kv.Key), kv.Value))
                    .Select(kv => kv.Key)
                    .ToHashSet(StringComparer.Ordinal);
                await ApplyFetchesAsync(table.Child, row.Values, rowChanged, row.Existing is null, user, errors,
                    $"{table.Field.Name}[{table.Rows.IndexOf(row)}].", ct, row.Existing);
            }
        }

        await ResolveAsync(); // a fetched value may have changed a link

        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var any = false;

            // Rows first (a total reads them), then the document (a row may read a document value).
            foreach (var table in tables.Where(t => t.Child.FormulaFields().Count > 0))
            {
                for (var i = 0; i < table.Rows.Count; i++)
                {
                    var row = table.Rows[i];
                    foreach (var field in table.Child.FormulaFields())
                    {
                        var context = Context(table.Child, row.Merged(), [], values, user, isNew, RowLinks(row));
                        any |= Compute(field, row.Merged(), row.Values, context, errors, $"{table.Field.Name}[{i}].", null);
                    }
                }
            }

            foreach (var field in meta.FormulaFields())
            {
                var context = Context(meta, values, tables, null, user, isNew, docLinked);
                any |= Compute(field, values, values, context, errors, string.Empty, changed);
            }

            if (!any)
                break;
        }
    }

    /// <summary>Evaluates one formula and stores the result if it differs from what the record has.</summary>
    private static bool Compute(
        DocFieldMeta field,
        IReadOnlyDictionary<string, object?> current,
        Dictionary<string, object?> target,
        ExpressionContext context,
        List<FieldError> errors,
        string pathPrefix,
        HashSet<string>? changed)
    {
        object? result;
        try
        {
            result = ToStorage(field, Expression.Parse(field.Formula!).Evaluate(context));
        }
        catch (FormatException ex)
        {
            if (!errors.Any(e => e.Field == pathPrefix + field.Name))
                errors.Add(new FieldError(pathPrefix + field.Name, $"The formula gave a value this field cannot hold: {ex.Message}"));
            return false;
        }

        if (Equals(current.GetValueOrDefault(field.Name), result))
            return false;

        target[field.Name] = result;
        changed?.Add(field.Name);
        return true;
    }

    private static object? ToStorage(DocFieldMeta field, object? result) =>
        result is null ? null : FieldValueConverter.ToStorage(field.Type, result);

    private static void ApplyExpressionDefaults(
        DocTypeMeta meta,
        Dictionary<string, object?> values,
        HashSet<string> changed,
        ExpressionContext context,
        List<FieldError> errors,
        string pathPrefix)
    {
        foreach (var field in meta.Fields.Where(f => f.Default is { Length: > 1 } d && d.StartsWith('=') && FieldTypes.HasColumn(f.Type)))
        {
            if (values.TryGetValue(field.Name, out var present) && present is not (null or ""))
                continue; // an empty value counts as "not given"

            try
            {
                var value = ToStorage(field, Expression.Parse(field.Default![1..]).Evaluate(context));
                if (value is not null)
                {
                    values[field.Name] = value;
                    changed.Add(field.Name);
                }
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(pathPrefix + field.Name, $"The default gave a value this field cannot hold: {ex.Message}"));
            }
        }
    }

    /// <summary>Copies values out of linked documents for fields with "fetch from", respecting what the user may read.</summary>
    private async Task ApplyFetchesAsync(
        DocTypeMeta meta,
        Dictionary<string, object?> values,
        HashSet<string> changed,
        bool isNew,
        IUserContext user,
        List<FieldError> errors,
        string pathPrefix,
        CancellationToken ct,
        Dictionary<string, object?>? existing = null)
    {
        foreach (var field in meta.Fields.Where(f => f.FetchFrom is not null))
        {
            var parts = field.FetchFrom!.Split('.');
            var linkName = parts[0];
            var current = (existing is null ? values : new Dictionary<string, object?>(existing).WithOverlay(values));

            if (!isNew && !changed.Contains(linkName))
                continue;

            var linkValue = current.GetValueOrDefault(linkName) as string;
            var fieldValue = current.GetValueOrDefault(field.Name);
            var fieldEmpty = fieldValue is null or "";

            if (string.IsNullOrEmpty(linkValue))
            {
                if (!field.FetchIfEmpty && fieldValue is not null && !isNew)
                {
                    values[field.Name] = null;
                    changed.Add(field.Name);
                }

                continue;
            }

            if (field.FetchIfEmpty && !fieldEmpty)
                continue;

            var link = meta.Fields.First(f => f.Name == linkName);
            if (link.Options is null || !registry.TryGet(link.Options, out var target))
                continue;

            var access = permissions.Evaluate(target, user, PermAction.Read);
            if (access == AccessLevel.None)
                continue; // never copy data the user could not read themselves

            var source = await repository.GetAsync(target, linkValue, ct);
            if (source is null)
                continue; // the link check reports the missing document

            if (access == AccessLevel.Owner && !string.Equals(source.GetValueOrDefault("owner") as string, user.User, StringComparison.Ordinal))
                continue;

            try
            {
                var copied = source.GetValueOrDefault(parts[1]);
                values[field.Name] = copied is null ? null : FieldValueConverter.ToStorage(field.Type, copied);
                changed.Add(field.Name);
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(pathPrefix + field.Name, $"The fetched value does not fit this field: {ex.Message}"));
            }
        }
    }

    /// <summary>Evaluates the DocType's validation rules and adds a message for each one that is false.</summary>
    public static void ValidateRules(
        DocTypeMeta meta, ExpressionContext context, List<FieldError> errors, string pathPrefix, string rowPath)
    {
        foreach (var rule in meta.Validations)
        {
            if (Expression.Parse(rule.Expression).IsTrue(context))
                continue;

            var path = rule.Field is null ? rowPath : pathPrefix + rule.Field;
            errors.Add(new FieldError(path, rule.Message));
        }
    }
}

internal static class DictionaryExtensions
{
    public static Dictionary<string, object?> WithOverlay(this Dictionary<string, object?> baseValues, IReadOnlyDictionary<string, object?> overlay)
    {
        foreach (var (key, value) in overlay)
            baseValues[key] = value;
        return baseValues;
    }
}
