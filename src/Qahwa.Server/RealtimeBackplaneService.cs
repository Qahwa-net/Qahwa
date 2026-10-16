using Qahwa.Core;

/// <summary>
/// Runs the database backplane for live events (ADR-086) when <c>Qahwa:Realtime:Backplane</c> is <c>database</c>: about once a second it
/// writes the events this server published and reads those of the other servers. Does nothing otherwise.
/// </summary>
public sealed class RealtimeBackplaneService(IConfiguration configuration, IServiceProvider services, ILogger<RealtimeBackplaneService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (services.GetService(typeof(RealtimeBackplane)) is not RealtimeBackplane backplane || configuration["Qahwa:Realtime:Backplane"] != "database")
            return;

        var interval = TimeSpan.FromMilliseconds(Math.Clamp(configuration.GetValue("Qahwa:Realtime:PollMilliseconds", 1000), 200, 10_000));
        using var timer = new PeriodicTimer(interval);
        var failing = false;
        do
        {
            try
            {
                await backplane.FlushAsync(stoppingToken);
                await backplane.PollAsync(stoppingToken);
                failing = false;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (!failing)
                    logger.LogWarning(ex, "The live-event backplane could not reach the database; live events stay on this server until it can.");
                failing = true;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
