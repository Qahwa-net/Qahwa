using System.Globalization;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record JobLogEntry(string Id, string Job, string Status, string? Message, long DurationMs, DateTime At);

/// <summary>
/// Scheduled Job Log: one row each time a scheduled job did something or failed (a quiet run that found nothing to do is not
/// recorded, so the log does not fill with noise). System Managers read it in the list of the DocType; old rows are removed after the
/// retention period.
/// </summary>
public sealed class JobLogService(IMetaRegistry registry, IDocumentRepository repository, HistoryRecorder history)
{
    public const string DocTypeName = "Scheduled Job Log";
    public const int DefaultRetentionDays = 30;
    private const int MaxMessage = 500;
    private const int MaxJob = 140;

    private DocTypeMeta? Meta => registry.TryGet(DocTypeName, out var meta) ? meta : null;

    private static string Clip(string? text, int max)
    {
        var line = new string((text ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    /// <summary>Writes one row. A failure to write the log never stops the job that is being logged.</summary>
    public async Task RecordAsync(string job, bool succeeded, string? message, TimeSpan duration, CancellationToken ct = default)
    {
        if (Meta is not { } meta)
            return;
        try
        {
            var row = HistoryRecorder.Standard("Administrator", history.Now());
            row["job"] = Clip(job, MaxJob);
            row["status"] = succeeded ? "Success" : "Failed";
            row["message"] = message is null ? null : Clip(message, MaxMessage);
            row["duration_ms"] = (long)Math.Max(0, duration.TotalMilliseconds);
            await repository.InsertAsync(meta, row, null, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // the log is a convenience; losing a line must not break the scheduler
        }
    }

    /// <summary>The most recent rows, newest first (System Managers only: the caller checks).</summary>
    public async Task<IReadOnlyList<JobLogEntry>> RecentAsync(int limit, string? status = null, CancellationToken ct = default)
    {
        if (Meta is not { } meta)
            return [];
        var filters = new List<QueryFilter>();
        if (status is "Success" or "Failed")
            filters.Add(new QueryFilter("status", FilterOp.Eq, status));
        var rows = await repository.ListAsync(meta, new ListQuery { Filters = filters, OrderBy = "creation", Descending = true, Limit = Math.Clamp(limit, 1, 200) }, ct);
        return rows.Select(r => new JobLogEntry((string)r["name"]!, r.GetValueOrDefault("job") as string ?? string.Empty, r.GetValueOrDefault("status") as string ?? string.Empty,
            r.GetValueOrDefault("message") as string, Convert.ToInt64(r.GetValueOrDefault("duration_ms") ?? 0L, CultureInfo.InvariantCulture), (DateTime)r["creation"]!)).ToList();
    }

    public async Task<int> PurgeAsync(TimeSpan retention, CancellationToken ct = default)
    {
        if (Meta is not { } meta)
            return 0;
        var cutoff = history.Now() - retention;
        var removed = 0;
        while (true)
        {
            var old = await repository.ListAsync(meta, new ListQuery { Filters = [new("creation", FilterOp.Lt, cutoff)], Fields = ["name"], Limit = 200 }, ct);
            if (old.Count == 0)
                return removed;
            foreach (var row in old)
            {
                await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
                removed++;
            }
        }
    }
}
