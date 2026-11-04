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

    /// <summary>
    /// Every job the scheduler knows (and any other job found in the log, such as an app's own), each with its last logged run and how many
    /// runs succeeded or failed in the last 24 hours. Jobs are logged when they did something or failed, so "last run" means "last time it had work".
    /// </summary>
    public async Task<IReadOnlyList<ScheduledJobInfo>> OverviewAsync(CancellationToken ct = default)
    {
        if (Meta is not { } meta)
            return [];
        var since = history.Now().AddHours(-24);
        var day = await repository.ListAsync(meta, new ListQuery { Filters = [new("creation", FilterOp.Ge, since)], Fields = ["job", "status"], OrderBy = "creation", Descending = true, Limit = 5000 }, ct);
        var counts = day.GroupBy(r => r.GetValueOrDefault("job") as string ?? string.Empty)
            .ToDictionary(g => g.Key, g => (Ok: g.Count(r => r.GetValueOrDefault("status") as string == "Success"), Failed: g.Count(r => r.GetValueOrDefault("status") as string == "Failed")));
        var known = SchedulerJobs.All.ToDictionary(j => j.Job, StringComparer.Ordinal);
        var names = SchedulerJobs.All.Select(j => j.Job).Concat(counts.Keys.Where(k => k.Length > 0 && !known.ContainsKey(k)).Order(StringComparer.Ordinal)).ToList();
        var result = new List<ScheduledJobInfo>();
        foreach (var name in names)
        {
            var last = await repository.ListAsync(meta, new ListQuery { Filters = [new("job", FilterOp.Eq, name)], OrderBy = "creation", Descending = true, Limit = 1 }, ct);
            var entry = last.Count == 0 ? null : new JobLogEntry((string)last[0]["name"]!, name, last[0].GetValueOrDefault("status") as string ?? string.Empty, last[0].GetValueOrDefault("message") as string,
                Convert.ToInt64(last[0].GetValueOrDefault("duration_ms") ?? 0L, CultureInfo.InvariantCulture), (DateTime)last[0]["creation"]!);
            var (ok, failed) = counts.GetValueOrDefault(name);
            known.TryGetValue(name, out var info);
            result.Add(new ScheduledJobInfo(name, info.Description ?? "A job of an installed app.", info.Pace ?? "its own schedule", entry, ok, failed));
        }

        return result;
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

public sealed record ScheduledJobInfo(string Job, string Description, string Pace, JobLogEntry? Last, int Succeeded24h, int Failed24h);

/// <summary>The steps the scheduler runs, as people should read them. Keep in step with <c>SchedulerService</c>.</summary>
public static class SchedulerJobs
{
    public const string EveryCycle = "every cycle";
    public const string Hourly = "hourly";

    public static readonly (string Job, string Description, string Pace)[] All =
    [
        ("installed apps", "Notices apps installed or uninstalled by another server or the command line.", EveryCycle),
        ("auto repeat", "Makes the documents that repeat on a schedule.", EveryCycle),
        ("reminders", "Tells people about documents at the time they asked.", EveryCycle),
        ("prepared reports", "Builds reports people asked to prepare in the background.", EveryCycle),
        ("workflow escalation", "Moves or flags documents that waited too long in a workflow state.", EveryCycle),
        ("data imports", "Runs large imports started in the background.", EveryCycle),
        ("emailed reports", "Sends reports that are emailed on a schedule.", EveryCycle),
        ("email digests", "Sends each person's digest of what happened.", EveryCycle),
        ("background jobs", "Runs work queued by scripts and apps, retrying what failed.", EveryCycle),
        ("scheduled scripts", "Runs server scripts set to a schedule.", EveryCycle),
        ("incoming mail", "Reads new mail from the site's mailbox.", EveryCycle),
        ("email queue", "Sends the emails waiting in the queue.", EveryCycle),
        ("webhooks", "Delivers the webhook calls waiting to go out.", EveryCycle),
        ("backup", "Writes the day's backup file to the backup folder, when one is set.", Hourly),
        ("clean-up", "Removes old copies of deleted documents and old log rows.", Hourly),
        ("app jobs", "Runs the scheduled jobs of installed apps, each when due.", EveryCycle),
        ("notification rules", "Tells people about documents by rules counted in days.", Hourly),
    ];
}
