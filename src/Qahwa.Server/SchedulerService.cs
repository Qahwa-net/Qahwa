using System.Diagnostics;
using Qahwa.Core;

/// <summary>
/// The scheduler: wakes up every few seconds-to-minutes and does the work that is due (repeating documents, queued email, webhooks,
/// emailed reports and digests, backups, rules, app jobs). It only ever calls code that is safe to run on several servers at once, so
/// no leader election is needed. Every step is written to the Scheduled Job Log when it did something or failed. Turn the scheduler off
/// with <c>Qahwa:Scheduler:Enabled = false</c>; change the pace with <c>Qahwa:Scheduler:IntervalSeconds</c> (10 to 3600, default 300).
/// </summary>
public sealed class SchedulerService(
    AutoRepeatService autoRepeat, NotificationRuleEngine notificationRules, EmailService email, WebhookService webhooks, ErrorLogService errorLog,
    BackupService backup, BackupOptions backupOptions, AppJobRunner appJobs, DeletedDocumentService deletedDocuments, AccessLogService accessLog, AutoEmailReportService emailedReports,
    EmailDigestService digests, AppInstaller installer, JobLogService jobLog, IConfiguration configuration, ILogger<SchedulerService> logger) : BackgroundService
{
    /// <summary>
    /// Runs one step. It returns what to say about the run, or null when there was nothing to do (nothing is logged then). A step that
    /// throws is written to the Error Log and the job log, and never stops the other steps.
    /// </summary>
    private async Task StepAsync(string job, Func<Task<string?>> run, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var message = await run();
            if (message is not null)
            {
                logger.LogInformation("{Job}: {Message}", job, message);
                await jobLog.RecordAsync(job, true, message, clock.Elapsed, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The scheduler could not run {Job}.", job);
            await errorLog.RecordAsync("Scheduler", null, $"The scheduler could not run {job}.", null, ex);
            await jobLog.RecordAsync(job, false, ex.Message, clock.Elapsed, ct);
        }
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
        var lastRules = DateTime.MinValue;
        var lastBackup = DateTime.MinValue;
        var lastPurge = DateTime.MinValue;
        do
        {
            // Apps uninstalled on this site (by another server or the command line) are switched off here too.
            await StepAsync("installed apps", async () => await installer.RefreshAsync(stoppingToken) ? "The set of installed apps changed." : null, stoppingToken);

            await StepAsync("auto repeat", async () =>
            {
                var run = await autoRepeat.RunDueAsync(stoppingToken);
                return run.Created + run.Failed > 0 ? $"{run.Created} document(s) made, {run.Failed} failed." : null;
            }, stoppingToken);

            await StepAsync("emailed reports", async () =>
            {
                var (sent, failed) = await emailedReports.RunDueAsync(stoppingToken);
                return sent + failed > 0 ? $"{sent} sent, {failed} failed." : null;
            }, stoppingToken);

            await StepAsync("email digests", async () =>
            {
                var (sent, _) = await digests.RunDueAsync(stoppingToken);
                return sent > 0 ? $"{sent} sent." : null;
            }, stoppingToken);

            await StepAsync("email queue", async () =>
            {
                var mail = await email.SendPendingAsync(stoppingToken);
                return mail.Sent + mail.Failed > 0 ? $"{mail.Sent} sent, {mail.Failed} failed." : null;
            }, stoppingToken);

            await StepAsync("webhooks", async () =>
            {
                var (sent, failed) = await webhooks.DeliverPendingAsync(stoppingToken);
                return sent + failed > 0 ? $"{sent} delivered, {failed} failed." : null;
            }, stoppingToken);

            // A backup file once a day into the configured folder (the first server to create today's file does the work).
            if (!string.IsNullOrWhiteSpace(backupOptions.Path) && DateTime.UtcNow - lastBackup >= TimeSpan.FromHours(1))
            {
                lastBackup = DateTime.UtcNow;
                await StepAsync("backup", async () =>
                {
                    var written = await backup.RunScheduledAsync(backupOptions.Path, backupOptions.Keep, new UserContext("system@qahwa", [UserContext.AdministratorRole]), stoppingToken);
                    return written is null ? null : $"Backup written to {Path.GetFileName(written)}.";
                }, stoppingToken);
            }

            // Copies of deleted documents and old job log rows are kept for a while (30 days unless the site says otherwise), then removed.
            if (DateTime.UtcNow - lastPurge >= TimeSpan.FromHours(1))
            {
                lastPurge = DateTime.UtcNow;
                await StepAsync("clean-up", async () =>
                {
                    var days = Math.Clamp(configuration.GetValue("Qahwa:DeletedDocuments:RetentionDays", DeletedDocumentService.DefaultRetentionDays), 1, 3650);
                    var copies = await deletedDocuments.PurgeAsync(TimeSpan.FromDays(days), stoppingToken);
                    var logDays = Math.Clamp(configuration.GetValue("Qahwa:JobLog:RetentionDays", JobLogService.DefaultRetentionDays), 1, 3650);
                    await jobLog.PurgeAsync(TimeSpan.FromDays(logDays), stoppingToken);
                    var accessDays = Math.Clamp(configuration.GetValue("Qahwa:AccessLog:RetentionDays", AccessLogService.DefaultRetentionDays), 1, 3650);
                    await accessLog.PurgeAsync(TimeSpan.FromDays(accessDays), stoppingToken);
                    return copies > 0 ? $"Removed {copies} old copy(ies) of deleted documents." : null;
                }, stoppingToken);
            }

            // The scheduled jobs of installed apps, each when it is due.
            await StepAsync("app jobs", async () =>
            {
                await appJobs.RunDueAsync(stoppingToken); // each job writes its own line to the job log
                return null;
            }, stoppingToken);

            // Rules counted in days from a date: once an hour is plenty, and each person is told once per day whatever happens.
            if (DateTime.UtcNow - lastRules >= TimeSpan.FromHours(1))
            {
                lastRules = DateTime.UtcNow;
                await StepAsync("notification rules", async () =>
                {
                    var told = await notificationRules.RunScheduledAsync(stoppingToken);
                    return told > 0 ? $"Told {told} person(s)." : null;
                }, stoppingToken);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
