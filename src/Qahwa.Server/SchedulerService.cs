using Qahwa.Core;


/// <summary>
/// The scheduler: wakes up every few seconds-to-minutes and makes the repeating documents that are due. It only ever calls
/// code that is safe to run on several servers at once, so no leader election is needed. Turn it off with
/// <c>Qahwa:Scheduler:Enabled = false</c>; change the pace with <c>Qahwa:Scheduler:IntervalSeconds</c> (10 to 3600, default 300).
/// </summary>
public sealed class SchedulerService(AutoRepeatService autoRepeat, NotificationRuleEngine notificationRules, IConfiguration configuration, ILogger<SchedulerService> logger) : BackgroundService
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
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
