using System.Globalization;
using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>An unvalidated filter, like the list's: ["status", "=", "Open"] as { field, op, value }.</summary>
public sealed record DashboardFilter(string Field, string Op, JsonElement? Value = null);

/// <summary>One tile of a dashboard: a big number, a chart, or a short list of documents.</summary>
public sealed record DashboardItem
{
    public const string Number = "Number";
    public const string Chart = "Chart";
    public const string List = "List";
    public static readonly string[] Types = [Number, Chart, List];
    public static readonly string[] ChartTypes = ["bar", "line", "pie"];
    public static readonly string[] Colors = ["gray", "blue", "green", "orange", "red", "purple"];
    public const int MaxFilters = 10;
    public const int MaxColumns = 8;
    public const int MaxRows = 50;

    /// <summary>Identifies the tile; made by the server when empty.</summary>
    public string Id { get; init; } = string.Empty;

    public required string Type { get; init; }
    public string Title { get; init; } = string.Empty;
    public required string DocType { get; init; }

    /// <summary>count, sum, avg, min or max (Number and Chart).</summary>
    public string Function { get; init; } = "count";

    /// <summary>The numeric field summarised (Number and Chart, anything but count).</summary>
    public string? ValueField { get; init; }

    /// <summary>Chart: the field whose values make the bars, points or slices.</summary>
    public string? GroupBy { get; init; }

    /// <summary>Chart over a date field: day, month or year.</summary>
    public string? Period { get; init; }

    public string ChartType { get; init; } = "bar";

    /// <summary>List: the fields shown, and how the rows are ordered.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    public string? OrderBy { get; init; }
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 10;
    public IReadOnlyList<DashboardFilter> Filters { get; init; } = [];
    public string Color { get; init; } = "blue";
}

/// <param name="IsPublic">Everyone who is signed in can see the dashboard (each sees only what their own permissions allow).</param>
public sealed record DashboardRequest(string? Title = null, bool IsPublic = false, IReadOnlyList<DashboardItem>? Items = null);

public sealed record DashboardInfo(string Id, string Title, bool IsPublic, bool Mine, string Owner, string? OwnerName, int Items, DateTime Modified);

public sealed record DashboardDetail(DashboardInfo Info, IReadOnlyList<DashboardItem> Items, bool CanEdit);

/// <summary>What one tile shows for the person looking: a number, the points of a chart, rows, or why it cannot be shown.</summary>
public sealed record DashboardResult(
    string Id,
    string Type,
    decimal? Number = null,
    IReadOnlyList<SeriesPoint>? Points = null,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<Dictionary<string, object?>>? Rows = null,
    bool Truncated = false,
    string? Error = null);

/// <summary>
/// Dashboards: tiles (numbers, charts, lists) over documents. A dashboard is a row of the Core DocType <c>Dashboard</c> with
/// its tiles as JSON; it is private to its owner or public to everyone signed in. Every tile is worked out with the
/// <b>viewer's</b> permissions (list rules, report right, User Permissions), so a shared dashboard can never show someone
/// more than they could see themselves; a tile they may not see says so instead.
/// </summary>
public sealed class DashboardService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    NotificationService notifications,
    HistoryRecorder history)
{
    public const string DocTypeName = "Dashboard";
    public const int MaxItems = 24;
    public const int MaxPerUser = 50;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private static IReadOnlyList<DashboardItem> ParseItems(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<DashboardItem>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<DashboardInfo> ToInfoAsync(Dictionary<string, object?> row, IUserContext user, CancellationToken ct)
    {
        var owner = (string)row["owner"]!;
        return new DashboardInfo(
            (string)row["name"]!, row["title"] as string ?? string.Empty, row["is_public"] is true, string.Equals(owner, user.User, StringComparison.Ordinal),
            owner, await notifications.NameOfAsync(owner, ct), ParseItems(row["definition"] as string).Count, (DateTime)row["modified"]!);
    }

    private bool Visible(Dictionary<string, object?> row, IUserContext user) =>
        row["is_public"] is true || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal) || IsManager(user);

    private async Task<Dictionary<string, object?>> RequireAsync(string id, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        return row is not null && Visible(row, user) ? row : throw new NotFoundException("Dashboard not found.");
    }

    private bool CanEdit(Dictionary<string, object?> row, IUserContext user) =>
        IsManager(user) || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal);

    /// <summary>The dashboards the person may open: their own, public ones, and (for a System Manager) all.</summary>
    public async Task<IReadOnlyList<DashboardInfo>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Meta, new ListQuery { OrderBy = "title", Descending = false, Limit = 500 }, ct);
        var result = new List<DashboardInfo>();
        foreach (var row in rows.Where(r => Visible(r, user)))
            result.Add(await ToInfoAsync(row, user, ct));
        return result;
    }

    public async Task<DashboardDetail> GetAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        return new DashboardDetail(await ToInfoAsync(row, user, ct), ParseItems(row["definition"] as string), CanEdit(row, user));
    }

    /// <summary>Checks a dashboard and gives every tile an id. Problems are keyed <c>items[2].groupBy</c>.</summary>
    public IReadOnlyList<DashboardItem> Validate(DashboardRequest request)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length is 0 or > 100 || title.Any(char.IsControl))
            throw Invalid("title", "Give the dashboard a title of up to 100 characters.");
        var items = request.Items ?? [];
        if (items.Count > MaxItems)
            throw Invalid("items", $"At most {MaxItems} tiles.");

        var errors = new List<FieldError>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DashboardItem>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = Clean(items[i]);
            if (item.Id.Length == 0 || !ids.Add(item.Id))
                item = item with { Id = NewId(ids) };
            ids.Add(item.Id);
            errors.AddRange(ValidateItem(item, i));
            result.Add(item);
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
        return result;
    }

    private static string NewId(HashSet<string> taken)
    {
        string id;
        do id = Guid.NewGuid().ToString("N")[..8]; while (taken.Contains(id));
        return id;
    }

    private static DashboardItem Clean(DashboardItem item) => item with
    {
        Id = new string((item.Id ?? string.Empty).Where(char.IsAsciiLetterOrDigit).Take(16).ToArray()),
        Type = item.Type?.Trim() ?? string.Empty,
        Title = (item.Title ?? string.Empty).Trim(),
        DocType = item.DocType?.Trim() ?? string.Empty,
        Function = (item.Function ?? "count").Trim().ToLowerInvariant(),
        ValueField = string.IsNullOrWhiteSpace(item.ValueField) ? null : item.ValueField.Trim(),
        GroupBy = string.IsNullOrWhiteSpace(item.GroupBy) ? null : item.GroupBy.Trim(),
        Period = string.IsNullOrWhiteSpace(item.Period) ? null : item.Period.Trim().ToLowerInvariant(),
        ChartType = (item.ChartType ?? "bar").Trim().ToLowerInvariant(),
        Columns = (item.Columns ?? []).Select(c => c?.Trim() ?? string.Empty).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
        OrderBy = string.IsNullOrWhiteSpace(item.OrderBy) ? null : item.OrderBy.Trim(),
        Filters = (item.Filters ?? []).Select(f => f with { Field = f.Field?.Trim() ?? string.Empty, Op = f.Op?.Trim() ?? string.Empty }).ToList(),
        Color = (item.Color ?? "blue").Trim().ToLowerInvariant(),
    };

    private IEnumerable<FieldError> ValidateItem(DashboardItem item, int index)
    {
        var errors = new List<FieldError>();
        void Add(string field, string message) => errors.Add(new FieldError($"items[{index}].{field}", message));

        if (!DashboardItem.Types.Contains(item.Type))
            Add("type", $"Use one of: {string.Join(", ", DashboardItem.Types)}.");
        if (item.Title.Length > 100 || item.Title.Any(char.IsControl))
            Add("title", "Keep the title to 100 characters of plain text.");
        if (!DashboardItem.Colors.Contains(item.Color))
            Add("color", $"Use one of: {string.Join(", ", DashboardItem.Colors)}.");
        if (!registry.TryGet(item.DocType, out var meta) || meta.IsChildTable)
        {
            Add("docType", "Choose a DocType.");
            return errors;
        }

        var columns = meta.ColumnTypes();
        if (item.Filters.Count > DashboardItem.MaxFilters)
            Add("filters", $"At most {DashboardItem.MaxFilters} filters.");
        for (var f = 0; f < Math.Min(item.Filters.Count, DashboardItem.MaxFilters); f++)
        {
            var filter = item.Filters[f];
            if (!columns.ContainsKey(filter.Field) || filter.Field.Length > 140)
                Add($"filters[{f}].field", $"'{(filter.Field.Length > 40 ? filter.Field[..40] : filter.Field)}' is not a field of {meta.Name}.");
            if (filter.Op.Length is 0 or > 12)
                Add($"filters[{f}].op", "Choose an operator.");
            if (filter.Value is { ValueKind: JsonValueKind.Object } || (filter.Value?.GetRawText().Length ?? 0) > 2000)
                Add($"filters[{f}].value", "Use a simple value or a list of them.");
        }

        if (item.Type is DashboardItem.Number or DashboardItem.Chart)
        {
            if (!new[] { "count", "sum", "avg", "min", "max" }.Contains(item.Function))
                Add("function", "Use count, sum, avg, min or max.");
            else if (item.Function != "count" && (item.ValueField is null || !columns.TryGetValue(item.ValueField, out var vt) || !IsNumeric(vt)))
                Add("valueField", "Choose a numeric field to summarise.");
        }

        if (item.Type == DashboardItem.Chart)
        {
            if (item.GroupBy is null || !columns.TryGetValue(item.GroupBy, out var gt) || gt == FieldType.Password)
                Add("groupBy", "Group by a field of this DocType.");
            else if (item.Period is not null && (gt is not (FieldType.Date or FieldType.Datetime) || !DocumentService.Periods.Contains(item.Period)))
                Add("period", "Only a date field can be grouped by day, month or year.");
            if (!DashboardItem.ChartTypes.Contains(item.ChartType))
                Add("chartType", $"Use one of: {string.Join(", ", DashboardItem.ChartTypes)}.");
        }

        if (item.Type == DashboardItem.List)
        {
            if (item.Columns.Count is 0 or > DashboardItem.MaxColumns || item.Columns.Any(c => !columns.TryGetValue(c, out var t) || t == FieldType.Password))
                Add("columns", $"Choose 1 to {DashboardItem.MaxColumns} fields to show.");
            if (item.OrderBy is not null && !columns.ContainsKey(item.OrderBy))
                Add("orderBy", "Order by a field of this DocType.");
            if (item.Limit is < 1 or > DashboardItem.MaxRows)
                Add("limit", $"Show 1 to {DashboardItem.MaxRows} rows.");
        }

        return errors;
    }

    private static bool IsNumeric(FieldType t) => t is FieldType.Int or FieldType.Float or FieldType.Decimal or FieldType.Currency or FieldType.Percent;

    public async Task<DashboardInfo> CreateAsync(DashboardRequest request, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var items = Validate(request);
        var mine = await repository.CountAsync(Meta, new ListQuery { Filters = [new("owner", FilterOp.Eq, user.User)] }, ct);
        if (mine >= MaxPerUser)
            throw Invalid("title", $"You can have at most {MaxPerUser} dashboards.");

        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["title"] = request.Title!.Trim();
        row["is_public"] = request.IsPublic;
        row["definition"] = JsonSerializer.Serialize(items, Json);
        await repository.InsertAsync(Meta, row, null, ct);
        return await ToInfoAsync((await repository.GetAsync(Meta, (string)row["name"]!, ct))!, user, ct);
    }

    public async Task<DashboardInfo> UpdateAsync(string id, DashboardRequest request, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        if (!CanEdit(row, user))
            throw new PermissionDeniedException("Only the owner of a dashboard can change it.");
        var items = Validate(request);
        var stored = (DateTime)row["modified"]!;
        if (expectedModified is not null && expectedModified != stored)
            throw new ConflictException("This dashboard was changed by someone else. Reload and try again.");

        var changes = new Dictionary<string, object?>
        {
            ["title"] = request.Title!.Trim(), ["is_public"] = request.IsPublic, ["definition"] = JsonSerializer.Serialize(items, Json),
            ["modified"] = history.Now() > stored ? history.Now() : stored.AddTicks(10), ["modified_by"] = user.User,
        };
        if (!await repository.UpdateAsync(Meta, id, changes, stored, null, ct))
            throw new ConflictException("This dashboard was changed by someone else. Reload and try again.");
        return await ToInfoAsync((await repository.GetAsync(Meta, id, ct))!, user, ct);
    }

    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        if (!CanEdit(row, user))
            throw new PermissionDeniedException("Only the owner of a dashboard can delete it.");
        await repository.DeleteAsync(Meta, id, null, ct);
    }

    /// <summary>Every tile of a dashboard, worked out for the person looking.</summary>
    public async Task<IReadOnlyList<DashboardResult>> DataAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        var results = new List<DashboardResult>();
        foreach (var item in ParseItems(row["definition"] as string))
            results.Add(await EvaluateAsync(item, user, ct));
        return results;
    }

    /// <summary>One tile, for the builder's preview. Checked like a saved one.</summary>
    public async Task<DashboardResult> PreviewAsync(DashboardItem item, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        item = Clean(item);
        var errors = ValidateItem(item, 0).Select(e => new FieldError(e.Field.Replace("items[0].", string.Empty), e.Message)).ToList();
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
        return await EvaluateAsync(item with { Id = item.Id.Length == 0 ? "preview" : item.Id }, user, ct);
    }

    private async Task<DashboardResult> EvaluateAsync(DashboardItem item, IUserContext user, CancellationToken ct)
    {
        var request = new ListRequest { Filters = item.Filters.Select(f => new RawFilter(f.Field, f.Op, f.Value)).ToList() };
        try
        {
            switch (item.Type)
            {
                case DashboardItem.Number:
                {
                    var (value, truncated) = await documents.AggregateAsync(item.DocType, item.Function, item.ValueField, request, user, ct);
                    return new DashboardResult(item.Id, item.Type, Number: value, Truncated: truncated);
                }

                case DashboardItem.Chart:
                {
                    var series = await documents.SeriesAsync(item.DocType, item.GroupBy ?? string.Empty, item.Period, item.Function, item.ValueField, request, user, ct);
                    return new DashboardResult(item.Id, item.Type, Points: series.Points, Truncated: series.Truncated);
                }

                default:
                {
                    var list = await documents.ListAsync(item.DocType, request with
                    {
                        Fields = item.Columns, OrderBy = item.OrderBy ?? "modified", Descending = item.Descending, Limit = Math.Clamp(item.Limit, 1, DashboardItem.MaxRows),
                    }, user, ct);
                    return new DashboardResult(item.Id, item.Type, Columns: item.Columns, Rows: list.Data, Truncated: list.Total > list.Data.Count);
                }
            }
        }
        catch (Exception ex) when (ex is PermissionDeniedException or NotFoundException or DocumentValidationException)
        {
            // One tile the viewer may not see, or that no longer fits its DocType, must not spoil the rest.
            var reason = ex is DocumentValidationException v ? string.Join(" ", v.Errors.Select(e => e.Message)) : "You do not have access to this.";
            return new DashboardResult(item.Id, item.Type, Error: reason.Length > 200 ? reason[..200] : reason);
        }
    }
}
