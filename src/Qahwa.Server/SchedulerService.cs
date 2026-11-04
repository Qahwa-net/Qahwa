using System.Diagnostics;
using Qahwa.Core;

/// <summary>
/// The scheduler: wakes up every few seconds-to-minutes and does the work that is due (repeating documents, queued email, webhooks,
/// emailed reports and digests, backups, rules, app jobs). It only ever calls code that is safe to run on several servers at once, so
/// no leader election is needed. Every step is written to the Scheduled Job Log when it did something or failed. Turn the scheduler off
/// with <c>Qahwa:Scheduler:Enabled = false</c>; change the pace with <c>Qahwa:Scheduler:IntervalSeconds</c> (10 to 3600, default 300).
/// </summary>
public sealed class SchedulerService(
    AutoRepeatService autoRepeat, ReminderService reminders, PreparedReportService preparedReports, WorkflowEscalationService escalations, ImportRunService importRuns, NotificationRuleEngine notificationRules, EmailService email, WebhookService webhooks, ErrorLogService errorLog,
    BackupService backup, BackupOptions backupOptions, AppJobRunner appJobs, DeletedDocumentService deletedDocuments, AccessLogService accessLog, AutoEmailReportService emailedReports,
    EmailDigestService digests, AppInstaller installer, OAuthService oauth, IncomingMailService incomingMail, BackgroundJobService backgroundJobs, ServerScriptEngine serverScripts, NotificationService notifications, JobLogService jobLog, IConfiguration configuration, ILogger<SchedulerService> logger) : BackgroundService
{
    /// <summary>
    /// Runs one step. It returns what to say about the run, or null when there was nothing to do (nothing is logged then, unless a person
    /// asked for the run). A step that throws is written to the Error Log and the job log, and never stops the other steps.
    /// </summary>
    private async Task<string?> StepAsync(string job, Func<CancellationToken, Task<string?>> run, bool byHand, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var message = await run(ct);
            if (message is not null || byHand)
            {
                var said = message ?? "Nothing was due.";
                logger.LogInformation("{Job}: {Message}", job, said);
                await jobLog.RecordAsync(job, true, byHand ? "Run by hand: " + said : said, clock.Elapsed, ct);
            }

            return message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The scheduler could not run {Job}.", job);
            await errorLog.RecordAsync("Scheduler", null, $"The scheduler could not run {job}.", null, ex);
            await jobLog.RecordAsync(job, false, byHand ? "Run by hand: " + ex.Message : ex.Message, clock.Elapsed, ct);
            return null;
        }
    }

    private Dictionary<string, Func<CancellationToken, Task<string?>>>? _steps;

    /// <summary>Every step by name, in the order the scheduler runs them. The names are those of <see cref="SchedulerJobs"/>.</summary>
    private Dictionary<string, Func<CancellationToken, Task<string?>>> Steps => _steps ??= new()
    {
        // Apps uninstalled on this site (by another server or the command line) are switched off here too.
        ["installed apps"] = async ct => await installer.RefreshAsync(ct) ? "The set of installed apps changed." : null,
        ["auto repeat"] = async ct =>
        {
            var run = await autoRepeat.RunDueAsync(ct);
            return run.Created + run.Failed > 0 ? $"{run.Created} document(s) made, {run.Failed} failed." : null;
        },
        ["reminders"] = async ct =>
        {
            var (sent, dropped) = await reminders.RunDueAsync(ct);
            return sent + dropped > 0 ? $"{sent} sent, {dropped} dropped (nobody to tell)." : null;
        },
        ["prepared reports"] = async ct =>
        {
            var (built, failed) = await preparedReports.RunDueAsync(ct);
            return built + failed > 0 ? $"{built} built, {failed} failed." : null;
        },
        ["workflow escalation"] = async ct =>
        {
            var (escalated, failed) = await escalations.RunDueAsync(ct);
            return escalated + failed > 0 ? $"{escalated} escalated, {failed} failed." : null;
        },
        ["data imports"] = async ct =>
        {
            var (done, failed) = await importRuns.RunDueAsync(ct);
            return done + failed > 0 ? $"{done} finished, {failed} failed." : null;
        },
        ["emailed reports"] = async ct =>
        {
            var (sent, failed) = await emailedReports.RunDueAsync(ct);
            return sent + failed > 0 ? $"{sent} sent, {failed} failed." : null;
        },
        ["email digests"] = async ct =>
        {
            var (sent, _) = await digests.RunDueAsync(ct);
            return sent > 0 ? $"{sent} sent." : null;
        },
        ["background jobs"] = async ct =>
        {
            var run = await backgroundJobs.RunDueAsync(ct);
            return run.Done + run.Failed + run.Retried > 0 ? $"{run.Done} done, {run.Retried} to try again, {run.Failed} failed." : null;
        },
        ["scheduled scripts"] = async ct =>
        {
            var (ran, failed) = await serverScripts.RunScheduledAsync(async (who, token) => (IUserContext?)await notifications.ContextOfAsync(who, token), ct);
            return ran + failed > 0 ? $"{ran} ran, {failed} failed." : null;
        },
        ["incoming mail"] = async ct =>
        {
            var kept = await incomingMail.PollAsync(ct);
            return kept > 0 ? $"{kept} message(s) kept." : null;
        },
        ["email queue"] = async ct =>
        {
            var mail = await email.SendPendingAsync(ct);
            return mail.Sent + mail.Failed > 0 ? $"{mail.Sent} sent, {mail.Failed} failed." : null;
        },
        ["webhooks"] = async ct =>
        {
            var (sent, failed) = await webhooks.DeliverPendingAsync(ct);
            return sent + failed > 0 ? $"{sent} delivered, {failed} failed." : null;
        },
        // A backup file once a day into the configured folder (the first server to create today's file does the work).
        ["backup"] = async ct =>
        {
            if (string.IsNullOrWhiteSpace(backupOptions.Path))
                return null;
            var written = await backup.RunScheduledAsync(backupOptions.Path, backupOptions.Keep, new UserContext("system@qahwa", [UserContext.AdministratorRole]), ct, backupOptions.Password);
            return written is null ? null : $"Backup written to {Path.GetFileName(written)}.";
        },
        // Copies of deleted documents and old job log rows are kept for a while (30 days unless the site says otherwise), then removed.
        ["clean-up"] = async ct =>
        {
            var days = Math.Clamp(configuration.GetValue("Qahwa:DeletedDocuments:RetentionDays", DeletedDocumentService.DefaultRetentionDays), 1, 3650);
            var copies = await deletedDocuments.PurgeAsync(TimeSpan.FromDays(days), ct);
            var logDays = Math.Clamp(configuration.GetValue("Qahwa:JobLog:RetentionDays", JobLogService.DefaultRetentionDays), 1, 3650);
            await jobLog.PurgeAsync(TimeSpan.FromDays(logDays), ct);
            var accessDays = Math.Clamp(configuration.GetValue("Qahwa:AccessLog:RetentionDays", AccessLogService.DefaultRetentionDays), 1, 3650);
            await accessLog.PurgeAsync(TimeSpan.FromDays(accessDays), ct);
            await oauth.PurgeAsync(ct);
            await backgroundJobs.PurgeAsync(ct);
            await importRuns.PurgeAsync(ct);
            return copies > 0 ? $"Removed {copies} old copy(ies) of deleted documents." : null;
        },
        // The scheduled jobs of installed apps, each when it is due.
        ["app jobs"] = async ct =>
        {
            await appJobs.RunDueAsync(ct); // each job writes its own line to the job log
            return null;
        },
        // Rules counted in days from a date: once an hour is plenty, and each person is told once per day whatever happens.
        ["notification rules"] = async ct =>
        {
            var told = await notificationRules.RunScheduledAsync(ct);
            return told > 0 ? $"Told {told} person(s)." : null;
        },
    };

    /// <summary>These steps wait an hour between runs when the scheduler runs them itself (a person can still run one at any time).</summary>
    private static readonly HashSet<string> Hourly = new(StringComparer.Ordinal) { "backup", "clean-up", "notification rules" };

    /// <summary>The names of the steps a person can run by hand.</summary>
    public IReadOnlyCollection<string> StepNames => Steps.Keys;

    /// <summary>
    /// Runs one step now, as asked by a System Manager, whether or not it is due by the clock. Every step is safe to run at the same time as
    /// the scheduler's own run, here or on another server. The run is logged (even when there was nothing to do).
    /// </summary>
    /// <returns>What the step said, or null when it was not found (see <see cref="StepNames"/>) .</returns>
    public async Task<string?> RunNowAsync(string job, CancellationToken ct)
    {
        if (!Steps.TryGetValue(job, out var run))
            return null;
        return await StepAsync(job, run, byHand: true, ct) ?? "Nothing was due.";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Qahwa:Scheduler:Enabled", true))
        {
            logger.LogInformation("The scheduler is turned off.");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("Qahwa:Scheduler:IntervalSeconds", 300), 10, 3600));
        using var timer = new PeriodicTimer(interval);
        var lastHourly = Steps.Keys.Where(Hourly.Contains).ToDictionary(k => k, _ => DateTime.MinValue, StringComparer.Ordinal);
        do
        {
            foreach (var (job, run) in Steps)
            {
                if (Hourly.Contains(job))
                {
                    if (DateTime.UtcNow - lastHourly[job] < TimeSpan.FromHours(1))
                        continue;
                    lastHourly[job] = DateTime.UtcNow;
                }

                await StepAsync(job, run, byHand: false, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
