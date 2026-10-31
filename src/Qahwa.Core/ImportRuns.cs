using System.Globalization;
using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>A background import, as its owner sees it.</summary>
public sealed record ImportRunInfo(
    string Id, string DocType, string Mode, string Status, int Rows, int Processed, int Created, int Updated, int Failed,
    IReadOnlyList<ImportRowError> Errors, bool MoreErrors, string? Message, bool CancelRequested, string User,
    DateTime Queued, DateTime? Started, DateTime? Finished);

/// <summary>
/// Imports of big files (up to 50,000 rows) that run in the background while the person carries on. The file is stored with the run, the
/// scheduler imports it one document at a time <b>as the person who asked</b> (so every right and rule applies exactly as for an
/// import they wait for), progress is written as it goes and the file is thrown away when the run ends. An import is never run
/// again after a crash: it could create everything twice, so an interrupted run is marked Failed and the report says how far it got.
/// </summary>
public sealed class ImportRunService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DataImportService import,
    NotificationService notifications,
    HistoryRecorder history,
    PermissionService permissions)
{
    public const string DocTypeName = "Import Run";
    public const int MaxActivePerUser = 2;
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private static int Int(object? value) => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static ImportRunInfo ToInfo(Dictionary<string, object?> row)
    {
        var errors = row["errors"] is string { Length: > 0 } text ? JsonSerializer.Deserialize<List<ImportRowError>>(text, Json) ?? [] : [];
        return new ImportRunInfo(
            (string)row["name"]!, (string)row["reference_doctype"]!, (string)(row["mode"] ?? "Insert"), (string)row["status"]!, Int(row["total_rows"]), Int(row["processed"]),
            Int(row["created"]), Int(row["updated"]), Int(row["failed"]), errors, row["more_errors"] is true, row["message"] as string,
            row["cancel_requested"] is true, (string)row["owner"]!, (DateTime)row["creation"]!, row["started_at"] as DateTime?, row["finished_at"] as DateTime?);
    }

    /// <summary>Checks the file and the person's rights now (so a bad file is refused at once, not hours later) and queues the import.</summary>
    public async Task<ImportRunInfo> StartAsync(string doctype, string csv, string mode, bool stopOnError, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = import.CheckLarge(doctype, csv, mode, user);

        var active = await repository.CountAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("status", FilterOp.In, new List<object?> { "Queued", "Running" })],
        }, ct);
        if (active >= MaxActivePerUser)
            throw new ConflictException($"You already have {MaxActivePerUser} imports waiting or running. Wait for one to finish.");

        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["reference_doctype"] = doctype;
        row["mode"] = mode;
        row["stop_on_error"] = stopOnError;
        row["status"] = "Queued";
        row["total_rows"] = (long)rows;
        row["processed"] = 0L;
        row["created"] = 0L;
        row["updated"] = 0L;
        row["failed"] = 0L;
        row["csv"] = csv;
        await repository.InsertAsync(Meta, row, null, ct);
        return ToInfo((await repository.GetAsync(Meta, (string)row["name"]!, ct))!);
    }

    private async Task<Dictionary<string, object?>> OwnRowAsync(string id, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        if (row is null || !(IsManager(user) || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal)))
            throw new NotFoundException("This import was not found.");
        return row;
    }

    public async Task<ImportRunInfo> GetAsync(string id, IUserContext user, CancellationToken ct = default) =>
        ToInfo(await OwnRowAsync(id, user, ct));

    /// <summary>The person's recent imports, newest first (a System Manager can ask for everyone's).</summary>
    public async Task<IReadOnlyList<ImportRunInfo>> ListAsync(IUserContext user, bool everyone = false, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        List<QueryFilter> filters = everyone && IsManager(user) ? [] : [new("owner", FilterOp.Eq, user.User)];
        var rows = await repository.ListAsync(Meta, new ListQuery { Filters = filters, OrderBy = "creation", Descending = true, Limit = 20 }, ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <summary>Stops an import: at once if it has not started, otherwise after the document it is on (what was imported stays).</summary>
    public async Task CancelAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await OwnRowAsync(id, user, ct);
        var modified = (DateTime)row["modified"]!;
        var status = (string)row["status"]!;
        var change = status switch
        {
            "Queued" => new Dictionary<string, object?> { ["status"] = "Cancelled", ["finished_at"] = history.Now(), ["csv"] = null, ["message"] = "Stopped before it started." },
            "Running" => new Dictionary<string, object?> { ["cancel_requested"] = true },
            _ => throw new ConflictException("This import is already finished."),
        };
        change["modified"] = Next(modified);
        if (!await repository.UpdateAsync(Meta, id, change, modified, null, ct))
            throw new ConflictException("This import just changed. Look again.");
    }

    /// <summary>
    /// Starts the oldest waiting import (safe on several servers: the run is claimed with a compare-and-swap) and marks imports that stopped
    /// reporting as failed. Called by the scheduler.
    /// </summary>
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
                ["status"] = "Failed", ["finished_at"] = now, ["csv"] = null, ["modified"] = Next(modified),
                ["message"] = "The import stopped before it finished (the server was restarted or lost). It was not run again, to avoid creating documents twice. Check what was imported, then import the rest.",
            }, modified, null, ct);
            failed++;
        }

        var waiting = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.Eq, "Queued")], OrderBy = "creation", Descending = false, Limit = 1,
        }, ct);
        foreach (var row in waiting)
        {
            var modified = (DateTime)row["modified"]!;
            var claim = new Dictionary<string, object?> { ["status"] = "Running", ["started_at"] = now, ["modified"] = Next(modified) };
            if (!await repository.UpdateAsync(Meta, (string)row["name"]!, claim, modified, null, ct))
                continue; // another server took it
            if (await ProcessAsync(row, (DateTime)claim["modified"]!, ct)) done++; else failed++;
        }

        return (done, failed);
    }

    private async Task<bool> ProcessAsync(Dictionary<string, object?> row, DateTime claimedAt, CancellationToken ct)
    {
        var id = (string)row["name"]!;
        var finish = new Dictionary<string, object?> { ["finished_at"] = history.Now(), ["csv"] = null };
        var ok = false;
        try
        {
            var person = await notifications.ContextOfAsync((string)row["owner"]!, ct)
                ?? throw new PermissionDeniedException("The person who asked for this import is not an enabled user any more.");
            var csv = (string)(row["csv"] ?? string.Empty);
            var cancelled = false;

            async Task<bool> Report(ImportProgress p)
            {
                var current = await repository.GetAsync(Meta, id, ct);
                cancelled = current?["cancel_requested"] is true;
                await repository.UpdateAsync(Meta, id, new Dictionary<string, object?>
                {
                    ["processed"] = (long)p.Processed, ["created"] = (long)p.Created, ["updated"] = (long)p.Updated, ["failed"] = (long)p.Failed,
                    ["modified"] = Next((DateTime)(current?["modified"] ?? claimedAt)),
                }, null, null, ct);
                return !cancelled;
            }

            var result = await import.RunAsync((string)row["reference_doctype"]!, csv, (string)(row["mode"] ?? "Insert"), row["stop_on_error"] is true, person, ct, large: true, progress: Report);
            finish["status"] = cancelled ? "Cancelled" : "Done";
            finish["processed"] = (long)(result.Created + result.Updated + result.Failed);
            finish["created"] = (long)result.Created;
            finish["updated"] = (long)result.Updated;
            finish["failed"] = (long)result.Failed;
            finish["errors"] = JsonSerializer.Serialize(result.Errors, Json);
            finish["more_errors"] = result.MoreErrors;
            finish["message"] = cancelled ? "Stopped at your request. What was imported before that stays." : result.Stopped ? "Stopped at the first failure, as asked." : null;
            ok = true;
        }
        catch (Exception ex) when (ex is QahwaException or FormatException or JsonException)
        {
            finish["status"] = "Failed";
            finish["message"] = new string(ex.Message.Select(c => char.IsControl(c) ? ' ' : c).Take(500).ToArray());
        }

        var current2 = await repository.GetAsync(Meta, id, ct);
        finish["modified"] = Next((DateTime)(current2?["modified"] ?? claimedAt));
        await repository.UpdateAsync(Meta, id, finish, null, null, ct);
        return ok;
    }

    /// <summary>Removes finished runs after a week (nothing sensitive is left in them: the file is deleted when the run ends).</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var old = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.In, new List<object?> { "Done", "Failed", "Cancelled" }), new("modified", FilterOp.Le, history.Now() - KeepFor)],
            Fields = ["name"], Limit = 200,
        }, ct);
        foreach (var row in old)
            await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
        return old.Count;
    }
}
