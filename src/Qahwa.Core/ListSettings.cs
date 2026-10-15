using System.Text.Json;
using System.Text.Json.Nodes;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>How one person likes to see a DocType's list: which columns, how many rows per page, and the sort order.</summary>
public sealed record ListViewSettings(IReadOnlyList<string> Columns, int PageSize, string? SortField, bool SortDescending);

/// <summary>A named set of list filters, kept for one person or shared with everyone who can use the DocType.</summary>
public sealed record SavedFilterInfo(string Id, string Name, JsonNode? Filters, bool IsPublic, bool Mine, string Owner, string? OwnerName);

/// <summary>Everything the list view needs to start: my settings (null for the defaults) and the filters I can pick from.</summary>
public sealed record ListViewState(ListViewSettings? Settings, IReadOnlyList<SavedFilterInfo> Filters);

/// <summary>
/// Per-person list settings and saved filters. Settings are validated against the DocType, so they can only ever choose
/// columns and sort fields the person may read; saved filters are checked for shape and for fields that exist, and are only
/// ever applied through the ordinary list rules, so they cannot reach anything a list could not.
/// </summary>
public sealed class ListSettingsService(
    IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, PermissionService permissions, HistoryRecorder history)
{
    public const string SettingsDocType = "List Setting";
    public const string FilterDocType = "Saved Filter";
    public const int MaxColumns = 15;
    public const int MaxFilters = 20;
    public const int MaxSavedPerUser = 50;
    public const int MaxNameLength = 60;
    private const int MaxFilterJson = 8000;

    public static readonly int[] PageSizes = [10, 20, 50, 100];
    private static readonly string[] Operators = ["=", "!=", ">", "<", ">=", "<=", "like", "is", "in"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DocTypeMeta SettingsMeta => registry.Get(SettingsDocType);
    private DocTypeMeta FilterMeta => registry.Get(FilterDocType);

    /// <summary>The DocType as this person may use it. Throws like opening the DocType would (no access, module blocked).</summary>
    private async Task<(DocTypeMeta Meta, FieldAccess Access)> RequireListAsync(string doctype, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var view = await documents.GetMetaViewAsync(doctype, user, ct);
        var meta = registry.Get(doctype);
        if (meta.IsChildTable || meta.IsSingle)
            throw new NotFoundException($"DocType '{doctype}' has no list.");
        return (meta, permissions.FieldAccessFor(meta, user, documentOwner: null, creating: false));
    }

    /// <summary>The columns of this DocType the person may show or sort by (standard columns and readable fields, never passwords).</summary>
    private static HashSet<string> Usable(DocTypeMeta meta, FieldAccess access)
    {
        var fields = meta.Fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        return meta.ColumnTypes().Keys
            .Where(c => fields.TryGetValue(c, out var f) ? f.Type != FieldType.Password && access.CanRead(f) : StandardFields.Columns.ContainsKey(c))
            .ToHashSet(StringComparer.Ordinal);
    }

    // ---- state ---------------------------------------------------------------------------------------------

    public async Task<ListViewState> GetAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        var (meta, access) = await RequireListAsync(doctype, user, ct);
        var usable = Usable(meta, access);

        ListViewSettings? settings = null;
        var row = await FindSettingsAsync(doctype, user.User, ct);
        if (row?.GetValueOrDefault("settings") is string json && TryParseSettings(json) is { } stored)
            settings = Normalize(stored, usable, throwOnError: false); // settings saved before a field was hidden quietly lose it

        return new ListViewState(settings, await FiltersAsync(doctype, user, ct));
    }

    private async Task<Dictionary<string, object?>?> FindSettingsAsync(string doctype, string user, CancellationToken ct) =>
        (await repository.ListAsync(SettingsMeta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("user", FilterOp.Eq, user)], Limit = 1,
        }, ct)).FirstOrDefault();

    private static ListViewSettings? TryParseSettings(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ListViewSettings>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ListViewSettings Normalize(ListViewSettings input, HashSet<string> usable, bool throwOnError)
    {
        var errors = new List<FieldError>();
        var columns = (input.Columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList();
        if (columns.Count > MaxColumns)
            errors.Add(new FieldError("columns", $"At most {MaxColumns} columns."));
        foreach (var bad in columns.Where(c => !usable.Contains(c)))
            errors.Add(new FieldError("columns", $"'{bad}' is not a column you can show."));

        var size = PageSizes.Contains(input.PageSize) ? input.PageSize : 20;
        if (input.PageSize != 0 && !PageSizes.Contains(input.PageSize))
            errors.Add(new FieldError("pageSize", $"Use one of {string.Join(", ", PageSizes)}."));

        var sort = string.IsNullOrWhiteSpace(input.SortField) ? null : input.SortField;
        if (sort is not null && !usable.Contains(sort))
            errors.Add(new FieldError("sortField", $"You cannot sort by '{sort}'."));

        if (throwOnError && errors.Count > 0)
            throw new DocumentValidationException(errors);

        return new ListViewSettings(columns.Where(usable.Contains).Take(MaxColumns).ToList(), size, sort is not null && usable.Contains(sort) ? sort : null, input.SortDescending);
    }

    public async Task<ListViewSettings> SaveSettingsAsync(string doctype, ListViewSettings input, IUserContext user, CancellationToken ct = default)
    {
        var (meta, access) = await RequireListAsync(doctype, user, ct);
        var settings = Normalize(input, Usable(meta, access), throwOnError: true);
        var now = history.Now();
        var json = JsonSerializer.Serialize(settings, Json);

        var existing = await FindSettingsAsync(doctype, user.User, ct);
        if (existing is not null)
        {
            await repository.UpdateAsync(SettingsMeta, (string)existing["name"]!,
                new Dictionary<string, object?> { ["settings"] = json, ["modified"] = now, ["modified_by"] = user.User }, null, null, ct);
            return settings;
        }

        var row = HistoryRecorder.Standard(user.User, now);
        row["reference_doctype"] = doctype;
        row["user"] = user.User;
        row["settings"] = json;
        await repository.InsertAsync(SettingsMeta, row, null, ct);
        return settings;
    }

    public async Task ResetSettingsAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        await RequireListAsync(doctype, user, ct);
        if (await FindSettingsAsync(doctype, user.User, ct) is { } row)
            await repository.DeleteAsync(SettingsMeta, (string)row["name"]!, null, ct);
    }

    // ---- saved filters -----------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<SavedFilterInfo>> FiltersAsync(string doctype, IUserContext user, CancellationToken ct)
    {
        async Task<IReadOnlyList<Dictionary<string, object?>>> Find(params QueryFilter[] filters) =>
            await repository.ListAsync(FilterMeta, new ListQuery
            {
                Filters = [new("reference_doctype", FilterOp.Eq, doctype), .. filters], OrderBy = "filter_name", Descending = false, Limit = 200,
            }, ct);

        var mine = await Find(new QueryFilter("owner", FilterOp.Eq, user.User));
        var shared = (await Find(new QueryFilter("is_public", FilterOp.Eq, true))).Where(r => !string.Equals(r["owner"] as string, user.User, StringComparison.Ordinal));
        var result = new List<SavedFilterInfo>();
        foreach (var row in mine.Concat(shared))
            result.Add(await ToInfoAsync(row, user, ct));
        return result;
    }

    private async Task<SavedFilterInfo> ToInfoAsync(Dictionary<string, object?> row, IUserContext user, CancellationToken ct)
    {
        var owner = (string)row["owner"]!;
        JsonNode? filters = null;
        try
        {
            filters = row["filters"] is string text ? JsonNode.Parse(text) : null;
        }
        catch (JsonException)
        {
            // a damaged row is shown without filters rather than breaking the list
        }

        var fullName = registry.TryGet("User", out var userMeta) ? (await repository.GetAsync(userMeta, owner, ct))?.GetValueOrDefault("full_name") as string : null;
        return new SavedFilterInfo((string)row["name"]!, (string)row["filter_name"]!, filters, UserPermissionService.IsTrue(row["is_public"]),
            string.Equals(owner, user.User, StringComparison.Ordinal), owner, fullName);
    }

    /// <summary>Checks the shape of a filter list and that every field exists and may be read; returns the clean list as JSON.</summary>
    private static string ValidateFilters(JsonElement filters, HashSet<string> usable)
    {
        if (filters.ValueKind != JsonValueKind.Array || filters.GetArrayLength() == 0 || filters.GetArrayLength() > MaxFilters)
            throw new DocumentValidationException([new FieldError("filters", $"Give 1 to {MaxFilters} filters.")]);

        var clean = new JsonArray();
        foreach (var item in filters.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3 || item[0].ValueKind != JsonValueKind.String || item[1].ValueKind != JsonValueKind.String)
                throw new DocumentValidationException([new FieldError("filters", "Each filter must look like [field, operator, value].")]);

            var field = item[0].GetString()!;
            var op = item[1].GetString()!;
            if (!usable.Contains(field))
                throw new DocumentValidationException([new FieldError("filters", $"'{field}' is not a field you can filter by.")]);
            if (!Operators.Contains(op))
                throw new DocumentValidationException([new FieldError("filters", $"'{op}' is not a filter operator.")]);

            var value = item[2];
            var simple = value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
            var list = value.ValueKind == JsonValueKind.Array && value.GetArrayLength() <= 50
                && value.EnumerateArray().All(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False);
            if (!simple && !list)
                throw new DocumentValidationException([new FieldError("filters", "A filter value must be text, a number, true or false, or a short list of those.")]);
            if (value.ValueKind == JsonValueKind.String && value.GetString()!.Length > 500)
                throw new DocumentValidationException([new FieldError("filters", "A filter value is too long.")]);

            clean.Add(new JsonArray(JsonValue.Create(field), JsonValue.Create(op), JsonNode.Parse(value.GetRawText())));
        }

        var text = clean.ToJsonString();
        return text.Length <= MaxFilterJson ? text : throw new DocumentValidationException([new FieldError("filters", "These filters are too long to save.")]);
    }

    public async Task<SavedFilterInfo> SaveFilterAsync(
        string doctype, string? name, JsonElement filters, bool isPublic, IUserContext user, CancellationToken ct = default)
    {
        var (meta, access) = await RequireListAsync(doctype, user, ct);
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0 || name.Length > MaxNameLength || name.Any(char.IsControl))
            throw new DocumentValidationException([new FieldError("name", $"Give the filter a name of 1 to {MaxNameLength} characters.")]);
        var json = ValidateFilters(filters, Usable(meta, access));

        // Sharing a view with everyone is for people who work with the DocType, not just read it.
        if (isPublic && permissions.Evaluate(meta, user, PermAction.Write) == AccessLevel.None)
            throw new PermissionDeniedException("Only people who can edit these documents can share a filter with everyone.");

        var existing = await repository.ListAsync(FilterMeta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("owner", FilterOp.Eq, user.User)], Limit = MaxSavedPerUser + 1,
        }, ct);
        var same = existing.FirstOrDefault(r => string.Equals(r["filter_name"] as string, name, StringComparison.OrdinalIgnoreCase));
        if (same is null && existing.Count >= MaxSavedPerUser)
            throw new DocumentValidationException([new FieldError("name", $"You can keep at most {MaxSavedPerUser} saved filters for a DocType.")]);

        var now = history.Now();
        Dictionary<string, object?> row;
        if (same is not null)
        {
            // Saving under the same name replaces it.
            await repository.UpdateAsync(FilterMeta, (string)same["name"]!,
                new Dictionary<string, object?> { ["filters"] = json, ["is_public"] = isPublic, ["modified"] = now, ["modified_by"] = user.User }, null, null, ct);
            row = same;
            row["filters"] = json;
            row["is_public"] = isPublic;
        }
        else
        {
            row = HistoryRecorder.Standard(user.User, now);
            row["filter_name"] = name;
            row["reference_doctype"] = doctype;
            row["filters"] = json;
            row["is_public"] = isPublic;
            await repository.InsertAsync(FilterMeta, row, null, ct);
        }

        return await ToInfoAsync(row, user, ct);
    }

    public async Task DeleteFilterAsync(string doctype, string id, IUserContext user, CancellationToken ct = default)
    {
        await RequireListAsync(doctype, user, ct);
        var row = await repository.GetAsync(FilterMeta, id, ct);
        // Only the owner (or an Administrator) can remove it; a filter of another DocType is the same as none.
        if (row is null || !string.Equals(row["reference_doctype"] as string, doctype, StringComparison.Ordinal))
            throw new NotFoundException("This filter was not found.");
        var mine = string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal);
        if (!mine && !(permissions.IsAdministrator(user) && UserPermissionService.IsTrue(row["is_public"])))
            throw new NotFoundException("This filter was not found.");

        await repository.DeleteAsync(FilterMeta, id, null, ct);
    }
}
