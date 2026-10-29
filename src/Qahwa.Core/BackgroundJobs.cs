using System.Globalization;
using System.Text.Json;

namespace Qahwa.Core;

/// <summary>What a queue handler is given: the data it was queued with, which attempt this is, and the services.</summary>
public sealed class QueueContext(string name, JsonElement payload, int attempt, IServiceProvider services, CancellationToken ct)
{
    public string Name { get; } = name;

    /// <summary>The data passed to <see cref="BackgroundJobService.EnqueueAsync"/> (JSON; <c>null</c> when none was given).</summary>
    public JsonElement Payload { get; } = payload;

    /// <summary>1 for the first try.</summary>
    public int Attempt { get; } = attempt;

    public IServiceProvider Services { get; } = services;

    /// <summary>Cancelled when the queue's time limit passes or the server stops.</summary>
    public CancellationToken Cancellation { get; } = ct;
}

public delegate Task QueueHandler(QueueContext context);

public sealed record QueueRun(int Done, int Failed, int Retried);

/// <summary>
/// A persistent background queue (ADR-117). An app registers a handler with <c>Queue(name, handler)</c> and queues work for it with
/// <see cref="EnqueueAsync"/>; the scheduler runs it on one of three queues (short, default, long, each with its own time limit and batch
/// size), retries a failure with a growing pause, and keeps a visible record (Background Job). Several servers can run the scheduler: a
/// job is claimed with a compare-and-swap on its row, so it runs on one server at a time, and one stuck "Running" after a crash is
/// queued again. A handler must be safe to run twice (it may be, after a crash or a timeout).
/// </summary>
public sealed class BackgroundJobService(
    IMetaRegistry registry, IDocumentRepository repository, AppCatalog catalog, IServiceProvider services, ErrorLogService errors, JobLogService? jobLog, HistoryRecorder history)
{
    public const string DocTypeName = "Background Job";
    public const int MaxPayloadBytes = 16 * 1024;
    public const int MaxDelayDays = 30;
    public const int MaxMessage = 500;

    public static readonly IReadOnlyDictionary<string, (TimeSpan Limit, int Batch)> Queues = new Dictionary<string, (TimeSpan, int)>(StringComparer.Ordinal)
    {
        ["short"] = (TimeSpan.FromSeconds(30), 20),
        ["default"] = (TimeSpan.FromMinutes(5), 10),
        ["long"] = (TimeSpan.FromMinutes(30), 2),
    };

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private DateTime Next(DateTime version) => history.Now() > version ? history.Now() : version.AddTicks(10);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    /// <summary>The pause before attempt <paramref name="attempt"/> + 1: 30 seconds, doubling, at most an hour.</summary>
    public static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Clamp(attempt - 1, 0, 10))));

    private static string Clean(string? text)
    {
        var line = new string((text ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length > MaxMessage ? line[..MaxMessage] : line;
    }

    /// <summary>Queues work for a handler registered by an installed app (named "app.name"). Returns the job's name.</summary>
    public async Task<string> EnqueueAsync(string handler, object? payload = null, string queue = "default", TimeSpan? delay = null, int maxAttempts = 3, CancellationToken ct = default)
    {
        if (!catalog.HasQueueHandler(handler))
            throw Invalid("handler", $"There is no queue handler '{handler}'.");
        if (!Queues.ContainsKey(queue))
            throw Invalid("queue", "The queue is short, default or long.");
        if (maxAttempts is < 1 or > 10)
            throw Invalid("max_attempts", "Between 1 and 10 attempts.");
        var wait = delay ?? TimeSpan.Zero;
        if (wait < TimeSpan.Zero || wait > TimeSpan.FromDays(MaxDelayDays))
            throw Invalid("delay", $"The delay is at most {MaxDelayDays} days.");
        var text = payload is null ? null : JsonSerializer.Serialize(payload);
        if (text is not null && System.Text.Encoding.UTF8.GetByteCount(text) > MaxPayloadBytes)
            throw Invalid("payload", $"The data is at most {MaxPayloadBytes / 1024} KB.");

        var row = HistoryRecorder.Standard("Administrator", history.Now());
        row["handler"] = handler;
        row["queue"] = queue;
        row["status"] = "Queued";
        row["attempts"] = 0;
        row["max_attempts"] = maxAttempts;
        row["run_after"] = history.Now() + wait;
        row["payload"] = text;
        await repository.InsertAsync(Meta, row, null, ct);
        return (string)row["name"]!;
    }

    /// <summary>Runs what is due on each queue. Called by the scheduler.</summary>
    public async Task<QueueRun> RunDueAsync(CancellationToken ct = default)
    {
        int done = 0, failed = 0, retried = 0;
        await RecoverStuckAsync(ct);
        foreach (var (queue, (limit, batch)) in Queues)
        {
            var now = history.Now();
            var due = await repository.ListAsync(Meta, new ListQuery
            {
                Filters = [new("status", FilterOp.Eq, "Queued"), new("queue", FilterOp.Eq, queue), new("run_after", FilterOp.Le, now)],
                OrderBy = "run_after", Descending = false, Limit = batch,
            }, ct);
            foreach (var row in due)
            {
                var outcome = await RunOneAsync(row, queue, limit, ct);
                if (outcome == 1) done++;
                else if (outcome == 2) failed++;
                else if (outcome == 3) retried++;
            }
        }

        return new QueueRun(done, failed, retried);
    }

    /// <summary>A job still "Running" long after its time limit means the server stopped in the middle: queue it again.</summary>
    private async Task RecoverStuckAsync(CancellationToken ct)
    {
        foreach (var (queue, (limit, _)) in Queues)
        {
            var cutoff = history.Now() - limit - TimeSpan.FromMinutes(1);
            var stuck = await repository.ListAsync(Meta, new ListQuery
            {
                Filters = [new("status", FilterOp.Eq, "Running"), new("queue", FilterOp.Eq, queue), new("started_at", FilterOp.Lt, cutoff)], Limit = 50,
            }, ct);
            foreach (var row in stuck)
            {
                var version = (DateTime)row["modified"]!;
                await repository.UpdateAsync(Meta, (string)row["name"]!, new Dictionary<string, object?>
                {
                    ["status"] = "Queued", ["run_after"] = history.Now(), ["last_error"] = "The server stopped while this was running.", ["modified"] = Next(version),
                }, version, null, ct);
            }
        }
    }

    /// <returns>0 when another server took it, 1 done, 2 failed for good, 3 queued to try again.</returns>
    private async Task<int> RunOneAsync(Dictionary<string, object?> row, string queue, TimeSpan limit, CancellationToken ct)
    {
        var name = (string)row["name"]!;
        var version = (DateTime)row["modified"]!;
        var attempt = Convert.ToInt32(row.GetValueOrDefault("attempts") ?? 0, CultureInfo.InvariantCulture) + 1;
        var max = Convert.ToInt32(row.GetValueOrDefault("max_attempts") ?? 3, CultureInfo.InvariantCulture);
        var started = history.Now();
        // Claim it: only one server gets past this.
        if (!await repository.UpdateAsync(Meta, name, new Dictionary<string, object?>
        {
            ["status"] = "Running", ["attempts"] = attempt, ["started_at"] = started, ["modified"] = Next(version),
        }, version, null, ct))
            return 0;

        var handlerName = (string)row["handler"]!;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        string? problem = null;
        try
        {
            var handler = catalog.QueueHandler(handlerName) ?? throw new InvalidOperationException("The handler is not installed.");
            var payload = row.GetValueOrDefault("payload") is string text && text.Length > 0 ? JsonDocument.Parse(text).RootElement.Clone() : default;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(limit);
            try
            {
                await handler(new QueueContext(handlerName, payload, attempt, services, cts.Token));
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"It took longer than the {queue} queue allows ({limit.TotalSeconds:0} seconds).");
            }
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            problem = Clean(e.Message);
            await errors.RecordAsync("Background job", null, handlerName, null, e);
        }

        var current = await repository.GetAsync(Meta, name, ct);
        var stamp = (DateTime)(current?["modified"] ?? DateTime.UtcNow);
        var update = new Dictionary<string, object?> { ["finished_at"] = history.Now(), ["modified"] = Next(stamp) };
        int outcome;
        if (problem is null)
        {
            update["status"] = "Done";
            update["last_error"] = null;
            outcome = 1;
        }
        else if (attempt >= max)
        {
            update["status"] = "Failed";
            update["last_error"] = problem;
            outcome = 2;
        }
        else
        {
            update["status"] = "Queued";
            update["last_error"] = problem;
            update["run_after"] = history.Now() + Backoff(attempt);
            outcome = 3;
        }

        await repository.UpdateAsync(Meta, name, update, null, null, ct);
        if (jobLog is not null)
            await jobLog.RecordAsync(handlerName, problem is null, problem ?? $"Done (attempt {attempt}).", timer.Elapsed, ct);
        return outcome;
    }

    /// <summary>Puts a failed job back in its queue for another round of attempts (System Managers; the caller checks).</summary>
    public async Task RetryAsync(string name, CancellationToken ct = default)
    {
        var row = await repository.GetAsync(Meta, name, ct) ?? throw new NotFoundException($"Background Job '{name}' was not found.");
        if (row.GetValueOrDefault("status") as string != "Failed")
            throw Invalid("status", "Only a failed job can be retried.");
        var version = (DateTime)row["modified"]!;
        if (!await repository.UpdateAsync(Meta, name, new Dictionary<string, object?>
        {
            ["status"] = "Queued", ["attempts"] = 0, ["run_after"] = history.Now(), ["finished_at"] = null, ["modified"] = Next(version),
        }, version, null, ct))
            throw new ConflictException("The job changed at the same time. Try again.");
    }

    /// <summary>Removes finished jobs after a while: done ones after a week, failed ones after a month.</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var (status, days) in new[] { ("Done", 7), ("Failed", 30) })
        {
            var cutoff = history.Now() - TimeSpan.FromDays(days);
            while (true)
            {
                var old = await repository.ListAsync(Meta, new ListQuery
                {
                    Filters = [new("status", FilterOp.Eq, status), new("finished_at", FilterOp.Lt, cutoff)], Fields = ["name"], Limit = 200,
                }, ct);
                if (old.Count == 0)
                    break;
                foreach (var row in old)
                {
                    await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
                    removed++;
                }
            }
        }

        return removed;
    }
}
