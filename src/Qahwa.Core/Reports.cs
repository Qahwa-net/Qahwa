using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record ReportAggregate(string Function, string? Field = null);

/// <summary>
/// What a report shows. Without <see cref="GroupBy"/> it is a list of documents (the chosen columns, filtered and ordered). With it, it is
/// a summary: one line per value of that field with up to five numbers (count, sum, average, smallest, largest).
/// </summary>
public sealed record ReportDefinition
{
    public const int MaxColumns = 20;
    public const int MaxFilters = 10;
    public const int MaxRows = 1000;
    public static readonly string[] Operators = ["=", "!=", ">", "<", ">=", "<=", "like", "in", "is"];

    public required string DocType { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<DashboardFilter> Filters { get; init; } = [];
    public string? OrderBy { get; init; }
    public bool Descending { get; init; } = true;
    public int Limit { get; init; } = 200;
    public string? GroupBy { get; init; }
    public IReadOnlyList<ReportAggregate> Aggregates { get; init; } = [];
}

/// <param name="IsPublic">Everyone signed in can open it (each sees only what their own permissions allow).</param>
public sealed record ReportRequest(string? Title = null, bool IsPublic = false, ReportDefinition? Definition = null);

public sealed record ReportInfo(string Id, string Title, string DocType, bool IsPublic, bool Mine, string Owner, string? OwnerName, DateTime Modified);

public sealed record ReportDetail(ReportInfo Info, ReportDefinition Definition, bool CanEdit);

public sealed record ReportColumn(string Key, string Label, string Type);

public sealed record ReportRun(IReadOnlyList<ReportColumn> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated, bool Grouped);

/// <summary>
/// The Report Builder: pick a DocType, the columns, filters and order (or a field to group by and the numbers to show), save it, and share
/// it. A report is a row of the Core DocType <c>Report</c> holding its definition as JSON; private to its owner or open to everyone signed
/// in. It is **run for the person looking**, through the same rules as the list (read, report and export rights, User Permissions, field
/// levels), so sharing a report can never show anyone more than they could already see.
/// </summary>
public sealed class ReportService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    NotificationService notifications,
    HistoryRecorder history)
{
    public const string DocTypeName = "Report";
    public const int MaxPerUser = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private static ReportDefinition? Parse(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<ReportDefinition>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool Visible(Dictionary<string, object?> row, IUserContext user) =>
        row["is_public"] is true || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal) || IsManager(user);

    private bool CanEdit(Dictionary<string, object?> row, IUserContext user) =>
        IsManager(user) || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal);

    private async Task<Dictionary<string, object?>> RequireAsync(string id, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        return row is not null && Visible(row, user) ? row : throw new NotFoundException("Report not found.");
    }

    private async Task<ReportInfo> ToInfoAsync(Dictionary<string, object?> row, IUserContext user, CancellationToken ct)
    {
        var owner = (string)row["owner"]!;
        return new ReportInfo((string)row["name"]!, row["title"] as string ?? string.Empty, row["reference_doctype"] as string ?? string.Empty, row["is_public"] is true,
            string.Equals(owner, user.User, StringComparison.Ordinal), owner, await notifications.NameOfAsync(owner, ct), (DateTime)row["modified"]!);
    }

    public async Task<IReadOnlyList<ReportInfo>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Meta, new ListQuery { OrderBy = "title", Descending = false, Limit = 500 }, ct);
        var result = new List<ReportInfo>();
        foreach (var row in rows.Where(r => Visible(r, user)))
            result.Add(await ToInfoAsync(row, user, ct));
        return result;
    }

    public async Task<ReportDetail> GetAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        var definition = Parse(row["definition"] as string) ?? throw new NotFoundException("This report's definition is damaged.");
        return new ReportDetail(await ToInfoAsync(row, user, ct), definition, CanEdit(row, user));
    }

    /// <summary>Checks and tidies a definition. Problems are keyed like <c>filters[2].field</c>.</summary>
    public ReportDefinition Validate(ReportDefinition? input)
    {
        if (input is null)
            throw Invalid("definition", "A report needs a definition.");
        var definition = input with
        {
            DocType = input.DocType?.Trim() ?? string.Empty,
            Columns = (input.Columns ?? []).Select(c => c?.Trim() ?? string.Empty).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            Filters = (input.Filters ?? []).Select(f => f with { Field = f.Field?.Trim() ?? string.Empty, Op = f.Op?.Trim().ToLowerInvariant() ?? string.Empty }).ToList(),
            OrderBy = string.IsNullOrWhiteSpace(input.OrderBy) ? null : input.OrderBy.Trim(),
            GroupBy = string.IsNullOrWhiteSpace(input.GroupBy) ? null : input.GroupBy.Trim(),
            Aggregates = (input.Aggregates ?? []).Select(a => new ReportAggregate((a.Function ?? string.Empty).Trim().ToLowerInvariant(), string.IsNullOrWhiteSpace(a.Field) ? null : a.Field.Trim())).ToList(),
        };

        var errors = new List<FieldError>();
        void Add(string field, string message) => errors.Add(new FieldError(field, message));

        if (!registry.TryGet(definition.DocType, out var meta) || meta.IsChildTable)
        {
            Add("docType", "Choose a DocType.");
            throw new DocumentValidationException(errors);
        }

        var columns = meta.ColumnTypes();
        bool Known(string field) => columns.TryGetValue(field, out var type) && type != FieldType.Password;

        if (definition.Filters.Count > ReportDefinition.MaxFilters)
            Add("filters", $"At most {ReportDefinition.MaxFilters} filters.");
        for (var i = 0; i < Math.Min(definition.Filters.Count, ReportDefinition.MaxFilters); i++)
        {
            var filter = definition.Filters[i];
            if (filter.Field.Length > 140 || !Known(filter.Field))
                Add($"filters[{i}].field", $"'{(filter.Field.Length > 40 ? filter.Field[..40] : filter.Field)}' is not a field of {meta.Name}.");
            if (!ReportDefinition.Operators.Contains(filter.Op))
                Add($"filters[{i}].op", "Choose an operator.");
            if (filter.Value is { ValueKind: JsonValueKind.Object } || (filter.Value?.GetRawText().Length ?? 0) > 2000)
                Add($"filters[{i}].value", "Use a simple value or a list of them.");
        }

        if (definition.Limit is < 1 or > ReportDefinition.MaxRows)
            Add("limit", $"Show 1 to {ReportDefinition.MaxRows} rows.");
        if (definition.OrderBy is not null && !columns.ContainsKey(definition.OrderBy))
            Add("orderBy", "Order by a field of this DocType.");

        if (definition.GroupBy is null)
        {
            if (definition.Columns.Count is 0 or > ReportDefinition.MaxColumns || definition.Columns.Any(c => !Known(c) && c != "name"))
                Add("columns", $"Choose 1 to {ReportDefinition.MaxColumns} fields to show.");
        }
        else
        {
            if (!Known(definition.GroupBy))
                Add("groupBy", "Group by a field of this DocType.");
            if (definition.Aggregates.Count is 0 or > 5)
                Add("aggregates", "Choose 1 to 5 numbers to show.");
            for (var i = 0; i < Math.Min(definition.Aggregates.Count, 5); i++)
            {
                var a = definition.Aggregates[i];
                if (!new[] { "count", "sum", "avg", "min", "max" }.Contains(a.Function))
                    Add($"aggregates[{i}].function", "Use count, sum, avg, min or max.");
                else if (a.Function != "count" && (a.Field is null || !columns.TryGetValue(a.Field, out var t) || t is not (FieldType.Int or FieldType.Float or FieldType.Decimal or FieldType.Currency or FieldType.Percent or FieldType.Rating or FieldType.Duration)))
                    Add($"aggregates[{i}].field", "Choose a numeric field.");
            }

            if (definition.Aggregates.Select(a => DocumentService.AggregateKey(a.Function, a.Field)).Distinct().Count() != definition.Aggregates.Count)
                Add("aggregates", "Each number can only be shown once.");
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
        return definition;
    }

    private static string CleanTitle(string? title)
    {
        var text = (title ?? string.Empty).Trim();
        if (text.Length is 0 or > 100 || text.Any(char.IsControl))
            throw Invalid("title", "Give the report a title of up to 100 characters.");
        return text;
    }

    public async Task<ReportInfo> CreateAsync(ReportRequest request, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var title = CleanTitle(request.Title);
        var definition = Validate(request.Definition);
        if (await repository.CountAsync(Meta, new ListQuery { Filters = [new("owner", FilterOp.Eq, user.User)] }, ct) >= MaxPerUser)
            throw Invalid("title", $"You can have at most {MaxPerUser} reports.");

        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["title"] = title;
        row["reference_doctype"] = definition.DocType;
        row["is_public"] = request.IsPublic;
        row["definition"] = JsonSerializer.Serialize(definition, Json);
        await repository.InsertAsync(Meta, row, null, ct);
        return await ToInfoAsync((await repository.GetAsync(Meta, (string)row["name"]!, ct))!, user, ct);
    }

    public async Task<ReportInfo> UpdateAsync(string id, ReportRequest request, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        if (!CanEdit(row, user))
            throw new PermissionDeniedException("Only the owner of a report can change it.");
        var title = CleanTitle(request.Title);
        var definition = Validate(request.Definition);
        var stored = (DateTime)row["modified"]!;
        if (expectedModified is not null && expectedModified != stored)
            throw new ConflictException("This report was changed by someone else. Reload and try again.");

        var changes = new Dictionary<string, object?>
        {
            ["title"] = title, ["reference_doctype"] = definition.DocType, ["is_public"] = request.IsPublic, ["definition"] = JsonSerializer.Serialize(definition, Json),
            ["modified"] = history.Now() > stored ? history.Now() : stored.AddTicks(10), ["modified_by"] = user.User,
        };
        if (!await repository.UpdateAsync(Meta, id, changes, stored, null, ct))
            throw new ConflictException("This report was changed by someone else. Reload and try again.");
        return await ToInfoAsync((await repository.GetAsync(Meta, id, ct))!, user, ct);
    }

    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        if (!CanEdit(row, user))
            throw new PermissionDeniedException("Only the owner of a report can delete it.");
        await repository.DeleteAsync(Meta, id, null, ct);
    }

    // ---- running ----

    public async Task<ReportRun> RunAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        var definition = Parse(row["definition"] as string) ?? throw new NotFoundException("This report's definition is damaged.");
        return await RunAsync(definition, user, ct);
    }

    /// <summary>Runs a definition for the person looking (checked first, so the builder can preview an unsaved one).</summary>
    public async Task<ReportRun> RunAsync(ReportDefinition definition, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        definition = Validate(definition);
        var meta = registry.Get(definition.DocType);
        var request = new ListRequest { Filters = definition.Filters.Select(f => new RawFilter(f.Field, f.Op, f.Value)).ToList() };

        if (definition.GroupBy is { } group)
        {
            var aggregates = definition.Aggregates.Select(a => (a.Function, a.Field)).ToList();
            var (rows, truncated) = await documents.GroupedAsync(definition.DocType, group, aggregates, request, user, ct);
            var columns = new List<ReportColumn> { new(group, LabelOf(meta, group), TypeOf(meta, group)) };
            columns.AddRange(definition.Aggregates.Select(a => new ReportColumn(DocumentService.AggregateKey(a.Function, a.Field), AggregateLabel(meta, a), "Float")));
            return new ReportRun(columns, rows, truncated, true);
        }

        var list = await documents.ListAsync(definition.DocType, request with
        {
            Fields = definition.Columns, OrderBy = definition.OrderBy ?? "modified", Descending = definition.Descending, Limit = definition.Limit,
        }, user, ct);
        return new ReportRun(definition.Columns.Select(c => new ReportColumn(c, LabelOf(meta, c), TypeOf(meta, c))).ToList(), list.Data, list.Total > list.Data.Count, false);
    }

    /// <summary>The report as a table for download: header texts and rows keyed by them. A list report needs the export right (like any export); a summary only needs the report right.</summary>
    public async Task<(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated, string Title)> ExportAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        var definition = Validate(Parse(row["definition"] as string));
        var title = row["title"] as string ?? "report";
        if (definition.GroupBy is not null)
        {
            var run = await RunAsync(definition, user, ct);
            return Labelled(run.Columns, run.Rows, run.Truncated, title);
        }

        var meta = registry.Get(definition.DocType);
        var export = await documents.ExportAsync(definition.DocType, new ListRequest
        {
            Filters = definition.Filters.Select(f => new RawFilter(f.Field, f.Op, f.Value)).ToList(),
            Fields = definition.Columns, OrderBy = definition.OrderBy ?? "modified", Descending = definition.Descending,
        }, user, ct);
        return Labelled(export.Columns.Select(c => new ReportColumn(c, LabelOf(meta, c), TypeOf(meta, c))).ToList(), export.Rows, export.Truncated, title);
    }

    private static (IReadOnlyList<string>, IReadOnlyList<Dictionary<string, object?>>, bool, string) Labelled(
        IReadOnlyList<ReportColumn> columns, IReadOnlyList<Dictionary<string, object?>> rows, bool truncated, string title)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var headers = columns.Select(c => used.Add(c.Label) ? c.Label : $"{c.Label} ({c.Key})").ToList();
        var table = rows.Select(r =>
        {
            var line = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Count; i++)
                line[headers[i]] = r.GetValueOrDefault(columns[i].Key);
            return line;
        }).ToList();
        return (headers, table, truncated, title);
    }

    private static string LabelOf(DocTypeMeta meta, string field) =>
        field == "name" ? "ID" : meta.Fields.FirstOrDefault(f => f.Name == field)?.Label ?? field;

    private static string TypeOf(DocTypeMeta meta, string field) =>
        meta.Fields.FirstOrDefault(f => f.Name == field)?.Type.ToString() ?? "Data";

    private static string AggregateLabel(DocTypeMeta meta, ReportAggregate a) => a.Function switch
    {
        "count" => "Count",
        "sum" => $"Total of {LabelOf(meta, a.Field!)}",
        "avg" => $"Average of {LabelOf(meta, a.Field!)}",
        "min" => $"Smallest {LabelOf(meta, a.Field!)}",
        _ => $"Largest {LabelOf(meta, a.Field!)}",
    };
}
