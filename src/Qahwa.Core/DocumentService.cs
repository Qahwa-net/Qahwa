using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// The document engine: permission checks, validation, naming, standard fields and optimistic
/// concurrency on top of an <see cref="IDocumentRepository"/>.
/// </summary>
public sealed class DocumentService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    PermissionService permissions,
    NamingService naming,
    TimeProvider clock)
{
    private const int MaxLimit = 1000;

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
        var meta = registry.Get(doctype);
        permissions.Require(meta, user, PermAction.Read);
        return meta;
    }

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
        var meta = registry.Get(doctype);
        permissions.Require(meta, user, PermAction.Create);
        if (meta.IsChildTable)
            throw new DocumentValidationException($"{meta.Name} is a child table and cannot be created directly.");

        var errors = new List<FieldError>();
        var values = Coerce(meta, input, errors, meta.Autoname == "prompt", out var promptName);
        ApplyDefaults(meta, values, errors);
        await ValidateAsync(meta, values, values.Keys, errors, ct);
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

        await repository.InsertAsync(meta, values, ct);
        return await repository.GetAsync(meta, name, ct)
            ?? throw new InvalidOperationException($"{meta.Name} '{name}' vanished after insert.");
    }

    public async Task<Dictionary<string, object?>> GetAsync(
        string doctype,
        string name,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        var access = permissions.Require(meta, user, PermAction.Read);
        var doc = await repository.GetAsync(meta, name, ct)
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
        var meta = registry.Get(doctype);
        var access = permissions.Require(meta, user, PermAction.Write);
        var existing = await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        permissions.RequireOwnership(access, existing, user);

        var errors = new List<FieldError>();
        var changes = Coerce(meta, input, errors, allowName: false, out _);

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

        await ValidateAsync(meta, merged, changes.Keys, errors, ct);
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
        if (changes.Count == 0)
            return existing;

        changes["modified"] = Now();
        changes["modified_by"] = user.User;
        if (!await repository.UpdateAsync(meta, name, changes, stored, ct))
            throw new ConflictException($"{meta.Name} '{name}' was changed or deleted by someone else. Reload and try again.");

        return await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
    }

    public async Task DeleteAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        var access = permissions.Require(meta, user, PermAction.Delete);
        var existing = await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        permissions.RequireOwnership(access, existing, user);

        if (!await repository.DeleteAsync(meta, name, ct))
            throw new NotFoundException($"{meta.Name} '{name}' was not found.");
    }

    public async Task<ListResult> ListAsync(
        string doctype,
        ListRequest request,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        var access = permissions.Require(meta, user, PermAction.Read);
        var query = Normalize(meta, request);

        if (access == AccessLevel.Owner)
            query = query with { Filters = [.. query.Filters, new QueryFilter("owner", FilterOp.Eq, user.User)] };

        var rows = await repository.ListAsync(meta, query, ct);
        var total = await repository.CountAsync(meta, query, ct);
        return new ListResult(rows.ToList(), total);
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
        out string? promptName)
    {
        promptName = null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, raw) in input)
        {
            if (key == "name" && allowName)
            {
                promptName = FieldValueConverter.Unwrap(raw) as string;
                continue;
            }

            // Standard fields are managed by the engine; clients may echo them back harmlessly.
            if (StandardFields.IsReserved(key))
                continue;

            var field = meta.Fields.FirstOrDefault(f => f.Name == key);
            if (field is null)
            {
                errors.Add(new FieldError(key, "Unknown field."));
                continue;
            }

            if (!FieldTypes.HasColumn(field.Type))
            {
                errors.Add(new FieldError(key, $"{field.Type} fields cannot be set directly yet."));
                continue;
            }

            try
            {
                var value = FieldValueConverter.ToStorage(field.Type, raw);
                values[key] = field.Lowercase && value is string text ? text.ToLowerInvariant() : value;
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(key, ex.Message));
            }
        }

        return values;
    }

    private static void ApplyDefaults(DocTypeMeta meta, Dictionary<string, object?> values, List<FieldError> errors)
    {
        foreach (var field in meta.Fields.Where(f => f.Default is not null && FieldTypes.HasColumn(f.Type)))
        {
            if (values.ContainsKey(field.Name))
                continue;

            try
            {
                values[field.Name] = FieldValueConverter.ToStorage(field.Type, field.Default);
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(field.Name, $"Invalid default: {ex.Message}"));
            }
        }
    }

    private async Task ValidateAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> document,
        IEnumerable<string> changed,
        List<FieldError> errors,
        CancellationToken ct)
    {
        var changedSet = changed.ToHashSet(StringComparer.Ordinal);

        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
        {
            var value = document.GetValueOrDefault(field.Name);
            var isEmpty = value is null or "";

            if (field.Required && isEmpty)
            {
                errors.Add(new FieldError(field.Name, "This field is required."));
                continue;
            }

            if (isEmpty || !changedSet.Contains(field.Name))
                continue;

            if (value is string text && FieldTypes.IsShortText(field.Type) && text.Length > FieldTypes.ShortTextLength)
                errors.Add(new FieldError(field.Name, $"Value is too long (max {FieldTypes.ShortTextLength} characters)."));

            if (field.Type == FieldType.Select && !field.SelectOptions().Contains(value as string, StringComparer.Ordinal))
                errors.Add(new FieldError(field.Name, $"Must be one of: {string.Join(", ", field.SelectOptions())}."));

            if (field.Type == FieldType.Link && value is string linked)
            {
                if (!registry.TryGet(field.Options!, out var target))
                    errors.Add(new FieldError(field.Name, $"Link target '{field.Options}' does not exist."));
                else if (!await repository.ExistsAsync(target, linked, ct))
                    errors.Add(new FieldError(field.Name, $"{target.Name} '{linked}' does not exist."));
            }
        }
    }

    private DateTime Now()
    {
        // PostgreSQL stores microseconds; truncate so values round-trip exactly.
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
