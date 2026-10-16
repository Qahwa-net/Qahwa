using Qahwa.Core;


/// <summary>
/// The scheduler: wakes up every few seconds-to-minutes and makes the repeating documents that are due. It only ever calls
/// code that is safe to run on several servers at once, so no leader election is needed. Turn it off with
/// <c>Qahwa:Scheduler:Enabled = false</c>; change the pace with <c>Qahwa:Scheduler:IntervalSeconds</c> (10 to 3600, default 300).
/// </summary>
public sealed class SchedulerService(AutoRepeatService autoRepeat, NotificationRuleEngine notificationRules, EmailService email, WebhookService webhooks, ErrorLogService errorLog, BackupService backup, BackupOptions backupOptions, AppJobRunner appJobs, DeletedDocumentService deletedDocuments, AutoEmailReportService emailedReports, IConfiguration configuration, ILogger<SchedulerService> logger) : BackgroundService
{
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
            try
            {
                var run = await autoRepeat.RunDueAsync(stoppingToken);
                if (run.Created + run.Failed > 0)
                    logger.LogInformation("Auto repeat made {Created} document(s), {Failed} failed.", run.Created, run.Failed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The scheduler could not run auto repeat.");
                await errorLog.RecordAsync("Scheduler", null, "The scheduler could not run auto repeat.", null, ex);
            }

            try
            {
                var (reportsSent, reportsFailed) = await emailedReports.RunDueAsync(stoppingToken);
                if (reportsSent + reportsFailed > 0)
                    logger.LogInformation("Emailed reports: {Sent} sent, {Failed} failed.", reportsSent, reportsFailed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The scheduler could not send emailed reports.");
                await errorLog.RecordAsync("Scheduler", null, "The scheduler could not send emailed reports.", null, ex);
            }

            try
            {
                var mail = await email.SendPendingAsync(stoppingToken);
                if (mail.Sent + mail.Failed > 0)
                    logger.LogInformation("Email queue: {Sent} sent, {Failed} failed.", mail.Sent, mail.Failed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The scheduler could not send queued email.");
                await errorLog.RecordAsync("Scheduler", null, "The scheduler could not send queued email.", null, ex);
            }

            try
            {
                var (sentHooks, failedHooks) = await webhooks.DeliverPendingAsync(stoppingToken);
                if (sentHooks + failedHooks > 0)
                    logger.LogInformation("Webhooks: {Sent} delivered, {Failed} failed.", sentHooks, failedHooks);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The scheduler could not deliver webhooks.");
                await errorLog.RecordAsync("Scheduler", null, "The scheduler could not deliver webhooks.", null, ex);
            }

            // A backup file once a day into the configured folder (the first server to create today's file does the work).
            if (!string.IsNullOrWhiteSpace(backupOptions.Path) && DateTime.UtcNow - lastBackup >= TimeSpan.FromHours(1))
            {
                lastBackup = DateTime.UtcNow;
                try
                {
                    var written = await backup.RunScheduledAsync(backupOptions.Path, backupOptions.Keep, new UserContext("system@qahwa", [UserContext.AdministratorRole]), stoppingToken);
                    if (written is not null)
                        logger.LogInformation("Backup written to {Path}.", written);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The scheduler could not make a backup.");
                    await errorLog.RecordAsync("Scheduler", null, "The scheduler could not make a backup.", null, ex);
                }
            }

            // Copies of deleted documents are kept for a while (30 days unless the site says otherwise), then removed.
            if (DateTime.UtcNow - lastPurge >= TimeSpan.FromHours(1))
            {
                lastPurge = DateTime.UtcNow;
                try
                {
                    var days = Math.Clamp(configuration.GetValue("Qahwa:DeletedDocuments:RetentionDays", DeletedDocumentService.DefaultRetentionDays), 1, 3650);
                    var purged = await deletedDocuments.PurgeAsync(TimeSpan.FromDays(days), stoppingToken);
                    if (purged > 0)
                        logger.LogInformation("Removed {Count} old copy(ies) of deleted documents.", purged);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The scheduler could not remove old copies of deleted documents.");
                    await errorLog.RecordAsync("Scheduler", null, "The scheduler could not remove old copies of deleted documents.", null, ex);
                }
            }

            // The scheduled jobs of installed apps, each when it is due.
            try
            {
                var ranJobs = await appJobs.RunDueAsync(stoppingToken);
                if (ranJobs > 0)
                    logger.LogInformation("App jobs: {Count} ran.", ranJobs);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The scheduler could not run app jobs.");
                await errorLog.RecordAsync("Scheduler", null, "The scheduler could not run app jobs.", null, ex);
            }

            // Rules counted in days from a date: once an hour is plenty, and each person is told once per day whatever happens.
            if (DateTime.UtcNow - lastRules >= TimeSpan.FromHours(1))
            {
                lastRules = DateTime.UtcNow;
                try
                {
                    var told = await notificationRules.RunScheduledAsync(stoppingToken);
                    if (told > 0)
                        logger.LogInformation("Notification rules told {Count} person(s).", told);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "The scheduler could not run notification rules.");
                await errorLog.RecordAsync("Scheduler", null, "The scheduler could not run notification rules.", null, ex);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
