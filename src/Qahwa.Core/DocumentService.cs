using System.Collections;
using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>
/// The document engine: permission checks, validation, naming, standard fields, child tables and optimistic
/// concurrency on top of an <see cref="IDocumentRepository"/>.
/// </summary>
public sealed class DocumentService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    PermissionService permissions,
    NamingService naming,
    TimeProvider clock)
{
    private readonly DocumentLogic _logic = new(registry, repository, permissions, clock);

    private const int MaxLimit = 1000;

    /// <summary>The most rows one child table of a document may hold.</summary>
    public const int MaxRows = 1000;

    private static readonly Dictionary<string, FilterOp> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = FilterOp.Eq,
        ["!="] = FilterOp.Ne,
        [">"] = FilterOp.Gt,
        ["<"] = FilterOp.Lt,
        [">="] = FilterOp.Ge,
        ["<="] = FilterOp.Le,
        ["like"] = FilterOp.Like,
        ["in"] = FilterOp.In,
        ["is"] = FilterOp.IsNull, // resolved below from the value ("set" / "not set")
    };

    public IReadOnlyList<DocTypeMeta> ListMetas(IUserContext user) =>
        registry.All
            .Where(m => !m.IsChildTable && permissions.Evaluate(m, user, PermAction.Read) != AccessLevel.None)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public DocTypeMeta GetMeta(string doctype, IUserContext user)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Read);
        return meta;
    }

    /// <summary>The child DocTypes used by the Table fields of a DocType, keyed by name (the Desk needs them to draw grids).</summary>
    public IReadOnlyDictionary<string, DocTypeMeta> GetChildMetas(DocTypeMeta meta) =>
        meta.Fields
            .Where(f => f.Type == FieldType.Table && f.Options is not null && registry.TryGet(f.Options, out _))
            .Select(f => registry.Get(f.Options!))
            .DistinctBy(c => c.Name)
            .ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);

    /// <summary>What the user may do with this DocType in general (owner-only rules count as allowed).</summary>
    public EffectivePermissions GetPermissions(DocTypeMeta meta, IUserContext user) => new(
        permissions.Evaluate(meta, user, PermAction.Read) != AccessLevel.None,
        permissions.Evaluate(meta, user, PermAction.Write) != AccessLevel.None,
        permissions.Evaluate(meta, user, PermAction.Create) != AccessLevel.None && !meta.IsChildTable,
        permissions.Evaluate(meta, user, PermAction.Delete) != AccessLevel.None);

    public async Task<Dictionary<string, object?>> InsertAsync(
        string doctype,
        IReadOnlyDictionary<string, object?> input,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Create);

        var errors = new List<FieldError>();
        var values = Coerce(meta, input, errors, meta.Autoname == "prompt", out var promptName);
        var tables = ReadTables(meta, input, errors);
        ApplyDefaults(meta, values, errors);
        foreach (var table in tables)
        {
            for (var i = 0; i < table.Rows.Count; i++)
                ApplyDefaults(table.Child, table.Rows[i].Values, errors, $"{table.Field.Name}[{i}].");
        }

        // Expression defaults, "fetch from" values and formulas, then validation of the finished document.
        var changed = values.Keys.ToHashSet(StringComparer.Ordinal);
        await _logic.RunAsync(meta, values, changed, tables, user, isNew: true, errors, ct);
        var context = _logic.Context(meta, values, tables, null, user, isNew: true);
        await ValidateAsync(meta, values, values.Keys, errors, string.Empty, context, ct);
        foreach (var table in tables)
            await ValidateTableAsync(table, null, errors, values, user, isNew: true, ct);
        CheckRequiredTables(meta, tables, errors, onlyIfProvided: false);
        ValidateRules(meta, tables, values, context, user, isNew: true, errors);
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var name = await naming.NextNameAsync(meta, values, promptName, ct);
        var now = Now();
        values["name"] = name;
        values["creation"] = now;
        values["modified"] = now;
        values["modified_by"] = user.User;
        values["owner"] = user.User;
        values["docstatus"] = 0;
        values["idx"] = 0;

        var children = tables
            .Select(t => BuildChanges(t, meta, name, new Dictionary<string, Dictionary<string, object?>>(), now, user))
            .Where(c => !c.IsEmpty)
            .ToList();
        await repository.InsertAsync(meta, values, children, ct);
        return await LoadAsync(meta, name, ct)
            ?? throw new InvalidOperationException($"{meta.Name} '{name}' vanished after insert.");
    }

    public async Task<Dictionary<string, object?>> GetAsync(
        string doctype,
        string name,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var access = permissions.Require(meta, user, PermAction.Read);
        var doc = await LoadAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        permissions.RequireOwnership(access, doc, user);
        return doc;
    }

    public async Task<Dictionary<string, object?>> UpdateAsync(
        string doctype,
        string name,
        IReadOnlyDictionary<string, object?> input,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var access = permissions.Require(meta, user, PermAction.Write);
        var existing = await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        permissions.RequireOwnership(access, existing, user);

        var errors = new List<FieldError>();
        var changes = Coerce(meta, input, errors, allowName: false, out _);
        var tables = ReadTables(meta, input, errors);

        // Only what really differs from the saved document counts as a change.
        foreach (var key in changes.Keys.ToList())
        {
            if (Equals(existing.GetValueOrDefault(key), changes[key]))
                changes.Remove(key);
        }

        // Optimistic concurrency: if the client says which version it edited, it must still be current.
        var stored = existing.GetValueOrDefault("modified") as DateTime?;
        if (input.TryGetValue("modified", out var rawModified) && FieldValueConverter.Unwrap(rawModified) is not null)
        {
            try
            {
                if (FieldValueConverter.ToStorage(FieldType.Datetime, rawModified) as DateTime? != stored)
                    throw new ConflictException(
                        $"{meta.Name} '{name}' was modified after you loaded it. Reload and try again.");
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError("modified", ex.Message));
            }
        }

        var merged = new Dictionary<string, object?>(existing);
        foreach (var (key, value) in changes)
            merged[key] = value;

        // Rows the client may refer to by name: the ones this document already has in each table.
        var currentRows = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = await repository.GetChildrenAsync(table.Child, meta.Name, name, table.Field.Name, ct);
            currentRows[table.Field.Name] = rows.ToDictionary(r => (string)r["name"]!, r => r, StringComparer.Ordinal);
            foreach (var row in table.Rows.Where(r => r.Name is not null && currentRows[table.Field.Name].ContainsKey(r.Name)))
                row.Existing = currentRows[table.Field.Name][row.Name!];
        }

        // Formulas and rules may read tables the client did not send: bring their saved rows into play unchanged.
        if (meta.HasLogic() || TableFields(meta).Any(f => registry.Get(f.Options!).HasLogic()))
        {
            foreach (var field in TableFields(meta).Where(f => tables.All(t => t.Field.Name != f.Name)).ToList())
            {
                var child = registry.Get(field.Options!);
                var rows = await repository.GetChildrenAsync(child, meta.Name, name, field.Name, ct);
                currentRows[field.Name] = rows.ToDictionary(r => (string)r["name"]!, r => r, StringComparer.Ordinal);
                tables.Add(new TableInput(field, child, rows.Select(r => new RowInput((string)r["name"]!, [], r)).ToList(), Provided: false));
            }
        }

        var changed = changes.Keys.ToHashSet(StringComparer.Ordinal);
        await _logic.RunAsync(meta, merged, changed, tables, user, isNew: false, errors, ct);
        foreach (var key in changed)
            changes[key] = merged[key]; // calculated and fetched values are saved too

        var context = _logic.Context(meta, merged, tables, null, user, isNew: false);
        await ValidateAsync(meta, merged, changed, errors, string.Empty, context, ct);
        foreach (var table in tables.Where(t => t.Provided))
            await ValidateTableAsync(table, currentRows[table.Field.Name], errors, merged, user, isNew: false, ct);

        CheckRequiredTables(meta, tables.Where(t => t.Provided).ToList(), errors, onlyIfProvided: true);
        ValidateRules(meta, tables, merged, context, user, isNew: false, errors);
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var now = Now();
        var children = tables
            .Select(t => BuildChanges(t, meta, name, currentRows[t.Field.Name], now, user))
            .Where(c => !c.IsEmpty)
            .ToList();
        if (changes.Count == 0 && children.Count == 0)
            return (await LoadAsync(meta, name, ct))!;

        changes["modified"] = now;
        changes["modified_by"] = user.User;
        if (!await repository.UpdateAsync(meta, name, changes, stored, children, ct))
            throw new ConflictException($"{meta.Name} '{name}' was changed or deleted by someone else. Reload and try again.");

        return await LoadAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
    }

    public async Task DeleteAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var access = permissions.Require(meta, user, PermAction.Delete);
        var existing = await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        permissions.RequireOwnership(access, existing, user);

        var childTables = TableFields(meta).Select(f => registry.Get(f.Options!)).ToList();
        if (!await repository.DeleteAsync(meta, name, childTables, ct))
            throw new NotFoundException($"{meta.Name} '{name}' was not found.");
    }

    public async Task<ListResult> ListAsync(
        string doctype,
        ListRequest request,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var access = permissions.Require(meta, user, PermAction.Read);
        var query = Normalize(meta, request);

        if (access == AccessLevel.Owner)
            query = query with { Filters = [.. query.Filters, new QueryFilter("owner", FilterOp.Eq, user.User)] };

        var rows = await repository.ListAsync(meta, query, ct);
        var total = await repository.CountAsync(meta, query, ct);
        return new ListResult(rows.ToList(), total);
    }

    /// <summary>Child tables are only reachable through their parent document, so they look like they do not exist.</summary>
    private static DocTypeMeta RequireTopLevel(DocTypeMeta meta) =>
        meta.IsChildTable ? throw new NotFoundException($"DocType '{meta.Name}' was not found.") : meta;

    private static IEnumerable<DocFieldMeta> TableFields(DocTypeMeta meta) =>
        meta.Fields.Where(f => f.Type == FieldType.Table && f.Options is not null);

    /// <summary>The document with its child rows attached under the Table field names.</summary>
    private async Task<Dictionary<string, object?>?> LoadAsync(DocTypeMeta meta, string name, CancellationToken ct)
    {
        var doc = await repository.GetAsync(meta, name, ct);
        if (doc is null)
            return null;

        foreach (var field in TableFields(meta))
        {
            var child = registry.Get(field.Options!);
            var rows = await repository.GetChildrenAsync(child, meta.Name, name, field.Name, ct);
            doc[field.Name] = rows.Select(r => ShapeRow(child, r)).ToList();
        }

        return doc;
    }

    /// <summary>A child row as the API shows it: its id, position and the DocType's own fields.</summary>
    private static Dictionary<string, object?> ShapeRow(DocTypeMeta child, Dictionary<string, object?> row)
    {
        var shaped = new Dictionary<string, object?> { ["name"] = row["name"], ["idx"] = row["idx"] };
        foreach (var field in child.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
            shaped[field.Name] = row.GetValueOrDefault(field.Name);
        return shaped;
    }

    private ListQuery Normalize(DocTypeMeta meta, ListRequest request)
    {
        var columns = meta.ColumnTypes();
        var errors = new List<FieldError>();

        List<string>? fields = null;
        if (request.Fields is { Count: > 0 })
        {
            fields = ["name"];
            foreach (var field in request.Fields.Where(f => f != "name").Distinct(StringComparer.Ordinal))
            {
                if (columns.ContainsKey(field))
                    fields.Add(field);
                else
                    errors.Add(new FieldError(field, "Unknown field."));
            }
        }

        var filters = new List<QueryFilter>();
        foreach (var raw in request.Filters)
        {
            if (!columns.TryGetValue(raw.Field, out var type))
            {
                errors.Add(new FieldError(raw.Field, "Cannot filter on an unknown field."));
                continue;
            }

            if (!Operators.TryGetValue(raw.Op, out var op))
            {
                errors.Add(new FieldError(raw.Field, $"Unsupported filter operator '{raw.Op}'."));
                continue;
            }

            try
            {
                filters.Add(BuildFilter(raw, type, op));
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(raw.Field, ex.Message));
            }
        }

        string? orderBy = request.OrderBy;
        if (orderBy is not null && !columns.ContainsKey(orderBy))
        {
            errors.Add(new FieldError(orderBy, "Cannot order by an unknown field."));
            orderBy = null;
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        return new ListQuery
        {
            Filters = filters,
            Fields = fields,
            OrderBy = orderBy,
            Descending = request.Descending,
            Limit = Math.Clamp(request.Limit, 1, MaxLimit),
            Offset = Math.Max(request.Offset, 0),
        };
    }

    private static QueryFilter BuildFilter(RawFilter raw, FieldType type, FilterOp op)
    {
        switch (op)
        {
            case FilterOp.IsNull:
                var state = (FieldValueConverter.Unwrap(raw.Value) as string)?.Trim().ToLowerInvariant();
                return state switch
                {
                    "set" or "not null" => new QueryFilter(raw.Field, FilterOp.IsNotNull),
                    "not set" or "null" => new QueryFilter(raw.Field, FilterOp.IsNull),
                    _ => throw new FormatException("Use 'set' or 'not set' with the 'is' operator."),
                };

            case FilterOp.In:
                var items = FieldValueConverter.UnwrapList(raw.Value)
                    .Select(v => FieldValueConverter.ToStorage(type, v))
                    .ToList();
                return new QueryFilter(raw.Field, op, items);

            case FilterOp.Like:
                if (!FieldTypes.IsTextual(type))
                    throw new FormatException("'like' only works on text fields.");
                return new QueryFilter(raw.Field, op, FieldValueConverter.ToStorage(FieldType.Data, raw.Value));

            default:
                var value = FieldValueConverter.ToStorage(type, raw.Value);
                if (value is null)
                    throw new FormatException("A value is required for this operator.");
                return new QueryFilter(raw.Field, op, value);
        }
    }

    /// <summary>Converts client input to storage types. Unknown or non-data fields become validation errors.</summary>
    private static Dictionary<string, object?> Coerce(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> input,
        List<FieldError> errors,
        bool allowName,
        out string? providedName,
        string pathPrefix = "")
    {
        providedName = null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, raw) in input)
        {
            if (key == "name" && allowName)
            {
                providedName = FieldValueConverter.Unwrap(raw) as string;
                continue;
            }

            // Standard fields are managed by the engine; clients may echo them back harmlessly.
            if (StandardFields.IsReserved(key))
                continue;

            var field = meta.Fields.FirstOrDefault(f => f.Name == key);
            if (field is null)
            {
                errors.Add(new FieldError(pathPrefix + key, "Unknown field."));
                continue;
            }

            if (field.Type == FieldType.Table)
                continue; // child rows are read separately by ReadTables

            if (!FieldTypes.HasColumn(field.Type))
            {
                errors.Add(new FieldError(pathPrefix + key, $"{field.Type} fields cannot be set directly yet."));
                continue;
            }

            try
            {
                var value = FieldValueConverter.ToStorage(field.Type, raw);
                values[key] = field.Lowercase && value is string text ? text.ToLowerInvariant() : value;
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(pathPrefix + key, ex.Message));
            }
        }

        return values;
    }

    /// <summary>Reads the rows the client sent for each Table field it mentioned (a missing field means "unchanged").</summary>
    private List<TableInput> ReadTables(DocTypeMeta meta, IReadOnlyDictionary<string, object?> input, List<FieldError> errors)
    {
        var tables = new List<TableInput>();
        foreach (var field in TableFields(meta))
        {
            if (!input.TryGetValue(field.Name, out var raw))
                continue;

            if (!registry.TryGet(field.Options!, out var child))
            {
                errors.Add(new FieldError(field.Name, $"The child DocType '{field.Options}' does not exist."));
                continue;
            }

            var rawRows = ReadRows(raw, field.Name, errors);
            if (rawRows.Count > MaxRows)
            {
                errors.Add(new FieldError(field.Name, $"A table can hold at most {MaxRows} rows."));
                continue;
            }

            var rows = new List<RowInput>();
            for (var i = 0; i < rawRows.Count; i++)
            {
                var values = Coerce(child, rawRows[i], errors, allowName: true, out var rowName, $"{field.Name}[{i}].");
                rows.Add(new RowInput(rowName, values));
            }

            tables.Add(new TableInput(field, child, rows));
        }

        return tables;
    }

    private static List<Dictionary<string, object?>> ReadRows(object? raw, string path, List<FieldError> errors)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (raw is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } || raw is null)
            return rows;

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    rows.Add(element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal));
                }
                else
                {
                    errors.Add(new FieldError($"{path}[{index}]", "Each row must be an object."));
                }

                index++;
            }

            return rows;
        }

        if (raw is IEnumerable items and not string)
        {
            var index = 0;
            foreach (var item in items)
            {
                if (item is IEnumerable<KeyValuePair<string, object?>> pairs)
                    rows.Add(pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
                else if (item is IEnumerable<KeyValuePair<string, JsonElement>> jsonPairs)
                    rows.Add(jsonPairs.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
                else
                    errors.Add(new FieldError($"{path}[{index}]", "Each row must be an object."));
                index++;
            }

            return rows;
        }

        errors.Add(new FieldError(path, "Must be a list of rows."));
        return rows;
    }

    /// <summary>Defaults starting with "=" are expressions; <see cref="DocumentLogic"/> evaluates those.</summary>
    private static bool IsExpressionDefault(string? value) => value is { Length: > 1 } && value[0] == '=';

    private static void ApplyDefaults(
        DocTypeMeta meta, Dictionary<string, object?> values, List<FieldError> errors, string pathPrefix = "")
    {
        foreach (var field in meta.Fields.Where(f => f.Default is not null && !IsExpressionDefault(f.Default) && FieldTypes.HasColumn(f.Type)))
        {
            if (values.TryGetValue(field.Name, out var present) && present is not (null or ""))
                continue; // an empty value counts as "not given"

            try
            {
                values[field.Name] = FieldValueConverter.ToStorage(field.Type, field.Default);
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(pathPrefix + field.Name, $"Invalid default: {ex.Message}"));
            }
        }
    }

    private async Task ValidateAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> document,
        IEnumerable<string> changed,
        List<FieldError> errors,
        string pathPrefix,
        ExpressionContext? context,
        CancellationToken ct)
    {
        var changedSet = changed.ToHashSet(StringComparer.Ordinal);

        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
        {
            var path = pathPrefix + field.Name;
            var value = document.GetValueOrDefault(field.Name);
            var isEmpty = value is null or "";

            // Required by the field or by its condition, and only while the field is shown.
            if (DocumentLogic.IsRequired(field, context) && isEmpty)
            {
                errors.Add(new FieldError(path, "This field is required."));
                continue;
            }

            if (isEmpty || !changedSet.Contains(field.Name))
                continue;

            if (value is string text && FieldTypes.IsShortText(field.Type) && text.Length > FieldTypes.ShortTextLength)
                errors.Add(new FieldError(path, $"Value is too long (max {FieldTypes.ShortTextLength} characters)."));

            if (field.Type == FieldType.Select && !field.SelectOptions().Contains(value as string, StringComparer.Ordinal))
                errors.Add(new FieldError(path, $"Must be one of: {string.Join(", ", field.SelectOptions())}."));

            if (field.Type == FieldType.Link && value is string linked)
            {
                if (!registry.TryGet(field.Options!, out var target))
                    errors.Add(new FieldError(path, $"Link target '{field.Options}' does not exist."));
                else if (!await repository.ExistsAsync(target, linked, ct))
                    errors.Add(new FieldError(path, $"{target.Name} '{linked}' does not exist."));
            }
        }
    }

    /// <summary>Validates the rows of one child table. <paramref name="current"/> is null for a brand new document.</summary>
    private async Task ValidateTableAsync(
        TableInput table,
        Dictionary<string, Dictionary<string, object?>>? current,
        List<FieldError> errors,
        IReadOnlyDictionary<string, object?> parentValues,
        IUserContext user,
        bool isNew,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var prefix = $"{table.Field.Name}[{i}].";

            if (row.Name is not null && current is not null)
            {
                if (!current.ContainsKey(row.Name))
                {
                    errors.Add(new FieldError(prefix + "name", "This row does not belong to the document."));
                    continue;
                }

                if (!seen.Add(row.Name))
                {
                    errors.Add(new FieldError(prefix + "name", "The same row appears twice."));
                    continue;
                }
            }

            var merged = row.Merged();
            var context = _logic.Context(table.Child, merged, [], parentValues, user, isNew);
            await ValidateAsync(table.Child, merged, row.Values.Keys, errors, prefix, context, ct);
        }
    }

    private static void CheckRequiredTables(
        DocTypeMeta meta, List<TableInput> tables, List<FieldError> errors, bool onlyIfProvided)
    {
        foreach (var field in TableFields(meta).Where(f => f.Required))
        {
            var provided = tables.FirstOrDefault(t => t.Field.Name == field.Name);
            if (provided is null ? !onlyIfProvided : provided.Rows.Count == 0)
                errors.Add(new FieldError(field.Name, "Add at least one row."));
        }
    }

    /// <summary>
    /// Runs the DocType's validation rules, and those of its child tables for each row. Rules read the finished
    /// document, so they only run once nothing more basic is wrong.
    /// </summary>
    private void ValidateRules(
        DocTypeMeta meta,
        List<TableInput> tables,
        IReadOnlyDictionary<string, object?> values,
        ExpressionContext context,
        IUserContext user,
        bool isNew,
        List<FieldError> errors)
    {
        if (errors.Count > 0)
            return;

        DocumentLogic.ValidateRules(meta, context, errors, string.Empty, string.Empty);
        foreach (var table in tables.Where(t => t.Provided && t.Child.Validations.Count > 0))
        {
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var rowContext = _logic.Context(table.Child, table.Rows[i].Merged(), [], values, user, isNew);
                DocumentLogic.ValidateRules(table.Child, rowContext, errors, $"{table.Field.Name}[{i}].", $"{table.Field.Name}[{i}]");
            }
        }
    }

    /// <summary>Works out which rows to add, change and remove so the table matches what the client sent.</summary>
    private static ChildChanges BuildChanges(
        TableInput table,
        DocTypeMeta parent,
        string parentName,
        Dictionary<string, Dictionary<string, object?>> current,
        DateTime now,
        IUserContext user)
    {
        var inserts = new List<IReadOnlyDictionary<string, object?>>();
        var updates = new List<ChildUpdate>();
        var kept = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var idx = i + 1;

            if (row.Name is not null && current.TryGetValue(row.Name, out var existing))
            {
                kept.Add(row.Name);
                var changed = row.Values
                    .Where(kv => !Equals(existing.GetValueOrDefault(kv.Key), kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                if (Convert.ToInt32(existing["idx"]) != idx)
                    changed["idx"] = idx;

                if (changed.Count > 0)
                {
                    changed["modified"] = now;
                    changed["modified_by"] = user.User;
                    updates.Add(new ChildUpdate(row.Name, changed));
                }

                continue;
            }

            inserts.Add(new Dictionary<string, object?>(row.Values, StringComparer.Ordinal)
            {
                ["name"] = Guid.NewGuid().ToString("N")[..10],
                ["parent"] = parentName,
                ["parenttype"] = parent.Name,
                ["parentfield"] = table.Field.Name,
                ["idx"] = idx,
                ["creation"] = now,
                ["modified"] = now,
                ["modified_by"] = user.User,
                ["owner"] = user.User,
                ["docstatus"] = 0,
            });
        }

        var deletes = current.Keys.Where(name => !kept.Contains(name)).ToList();
        return new ChildChanges(table.Child, inserts, updates, deletes);
    }

    private DateTime Now()
    {
        // PostgreSQL stores microseconds; truncate so values round-trip exactly.
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
