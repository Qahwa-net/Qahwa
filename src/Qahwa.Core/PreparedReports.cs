using System.Globalization;
using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>A prepared report, as its owner sees it (without the rows: see <see cref="PreparedReportResult"/>).</summary>
public sealed record PreparedReportInfo(
    string Id, string Report, string? Title, string Status, int Rows, bool Truncated, string? Message, DateTime Requested, DateTime? Started, DateTime? Finished);

/// <param name="Columns">The header texts, in order.</param>
/// <param name="Rows">One dictionary per row, keyed by header text.</param>
public sealed record PreparedReportResult(PreparedReportInfo Info, IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows);

/// <summary>
/// Prepared reports: a report that is slow to run (many documents, a calculation) is queued, built by the scheduler in the background
/// <b>as the person who asked</b> (so their permissions decide every row), and kept for a week to open or download whenever they like.
/// It reads the same table the report's download does, so a prepared report is not cut at the 1,000 rows a live run shows. A report is
/// built from scratch each time, so a run that is interrupted is simply tried again.
/// </summary>
public sealed class PreparedReportService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    ReportService reports,
    NotificationService notifications,
    HistoryRecorder history,
    PermissionService permissions)
{
    public const string DocTypeName = "Prepared Report";
    public const int MaxActivePerUser = 3;
    public const int MaxResultBytes = 8 * 1024 * 1024;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private static PreparedReportInfo ToInfo(Dictionary<string, object?> row) => new(
        (string)row["name"]!, (string)row["report"]!, row.GetValueOrDefault("report_title") as string, row.GetValueOrDefault("status") as string ?? "Queued",
        row.GetValueOrDefault("row_count") is { } n ? Convert.ToInt32(n, CultureInfo.InvariantCulture) : 0, row.GetValueOrDefault("truncated") is true,
        row.GetValueOrDefault("message") as string, (DateTime)row["creation"]!, row.GetValueOrDefault("started_at") as DateTime?, row.GetValueOrDefault("finished_at") as DateTime?);

    /// <summary>Queues a report for the person (who must be able to open it). Asking again while one is waiting or running returns that one.</summary>
    public async Task<PreparedReportInfo> RequestAsync(string reportId, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var detail = await reports.GetAsync(reportId, user, ct); // proves it exists and may be opened

        var active = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("status", FilterOp.In, new List<object?> { "Queued", "Running" })],
            OrderBy = "creation", Descending = false, Limit = MaxActivePerUser + 1,
        }, ct);
        if (active.FirstOrDefault(r => (string)r["report"]! == reportId) is { } same)
            return ToInfo(same);
        if (active.Count >= MaxActivePerUser)
            throw new ConflictException($"You already have {MaxActivePerUser} reports being prepared. Wait for one to finish.");

        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["report"] = reportId;
        row["report_title"] = detail.Info.Title;
        row["status"] = "Queued";
        await repository.InsertAsync(Meta, row, null, ct);
        return ToInfo((await repository.GetAsync(Meta, (string)row["name"]!, ct))!);
    }

    private async Task<Dictionary<string, object?>> OwnRowAsync(string id, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        if (row is null || !(IsManager(user) || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal)))
            throw new NotFoundException("This prepared report was not found.");
        return row;
    }

    /// <summary>The person's prepared versions of one report, newest first.</summary>
    public async Task<IReadOnlyList<PreparedReportInfo>> ListAsync(string reportId, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("report", FilterOp.Eq, reportId)],
            OrderBy = "creation", Descending = true, Limit = 20,
        }, ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <summary>A finished prepared report with its rows.</summary>
    public async Task<PreparedReportResult> GetAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await OwnRowAsync(id, user, ct);
        var info = ToInfo(row);
        if (info.Status != "Done" || row.GetValueOrDefault("result") is not string json)
            return new PreparedReportResult(info, [], []);
        using var parsed = JsonDocument.Parse(json);
        var columns = parsed.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString() ?? string.Empty).ToList();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var r in parsed.RootElement.GetProperty("rows").EnumerateArray())
            rows.Add(r.EnumerateObject().ToDictionary(p => p.Name, p => (object?)ValueOf(p.Value), StringComparer.Ordinal));
        return new PreparedReportResult(info, columns, rows);
    }

    private static object? ValueOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        _ => value.GetString(),
    };

    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await OwnRowAsync(id, user, ct);
        if (row["status"] as string == "Running")
            throw new ConflictException("This report is being prepared right now.");
        await repository.DeleteAsync(Meta, id, null, ct);
    }

    /// <summary>Builds the oldest waiting report (several servers may call this: the run is claimed with a compare-and-swap), marks ones that stopped reporting as failed, and deletes old ones.</summary>
    /// <returns>How many reports were built and how many failed.</returns>
    public async Task<(int Done, int Failed)> RunDueAsync(CancellationToken ct = default)
    {
        var now = history.Now();
        int done = 0, failed = 0;

        foreach (var stale in await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.Eq, "Running"), new("modified", FilterOp.Le, now - StaleAfter)], Limit = 20,
        }, ct))
        {
            var modified = (DateTime)stale["modified"]!;
            await repository.UpdateAsync(Meta, (string)stale["name"]!, new Dictionary<string, object?>
            {
                ["status"] = "Failed", ["finished_at"] = now, ["modified"] = Next(modified),
                ["message"] = "The report stopped before it finished (the server was restarted or lost). Prepare it again.",
            }, modified, null, ct);
            failed++;
        }

        foreach (var old in await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.In, new List<object?> { "Done", "Failed" }), new("creation", FilterOp.Le, now - KeepFor)], Fields = ["name"], Limit = 50,
        }, ct))
            await repository.DeleteAsync(Meta, (string)old["name"]!, null, ct);

        foreach (var row in await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.Eq, "Queued")], OrderBy = "creation", Descending = false, Limit = 1,
        }, ct))
        {
            var modified = (DateTime)row["modified"]!;
            var claim = new Dictionary<string, object?> { ["status"] = "Running", ["started_at"] = now, ["modified"] = Next(modified) };
            if (!await repository.UpdateAsync(Meta, (string)row["name"]!, claim, modified, null, ct))
                continue; // another server took it
            if (await BuildAsync(row, (DateTime)claim["modified"]!, ct)) done++; else failed++;
        }

        return (done, failed);
    }

    private async Task<bool> BuildAsync(Dictionary<string, object?> row, DateTime claimedAt, CancellationToken ct)
    {
        var finish = new Dictionary<string, object?> { ["finished_at"] = history.Now() };
        var ok = false;
        try
        {
            var person = await notifications.ContextOfAsync((string)row["owner"]!, ct)
                ?? throw new PermissionDeniedException("The person who asked for this report is not an enabled user any more.");
            var (columns, rows, truncated, _) = await reports.ExportAsync((string)row["report"]!, person, ct);
            var json = JsonSerializer.Serialize(new { columns, rows, truncated }, Json);
            if (json.Length > MaxResultBytes)
                throw new ConflictException("The report is too large to keep. Add a filter or fewer columns.");
            finish["status"] = "Done";
            finish["result"] = json;
            finish["row_count"] = (long)rows.Count;
            finish["truncated"] = truncated;
            ok = true;
        }
        catch (Exception ex) when (ex is QahwaException)
        {
            finish["status"] = "Failed";
            var reason = ex is DocumentValidationException v ? string.Join(" ", v.Errors.Select(e => e.Message)) : ex.Message;
            finish["message"] = new string(reason.Select(c => char.IsControl(c) ? ' ' : c).Take(500).ToArray());
        }

        finish["modified"] = Next(claimedAt);
        await repository.UpdateAsync(Meta, (string)row["name"]!, finish, null, null, ct);
        return ok;
    }
}
