using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class JobLogTests
{
    private readonly TestEngine _engine = new();

    private sealed class JobApp(Func<IServiceProvider, CancellationToken, Task> good, Func<IServiceProvider, CancellationToken, Task> bad) : IQahwaApp
    {
        public string Name => "jobs";
        public string Version => "1.0.0";

        public void Configure(IAppBuilder app) => app.Schedule("good", TimeSpan.FromMinutes(5), good).Schedule("bad", TimeSpan.FromMinutes(5), bad);
    }

    [Fact]
    public async Task Each_app_job_run_is_logged_with_its_outcome_newest_first()
    {
        _engine.AppCatalog.Add(new JobApp((_, _) => Task.CompletedTask, (_, _) => throw new InvalidOperationException("it broke\r\nBcc: x")));

        await _engine.AppJobs.RunDueAsync();

        var log = await _engine.JobLog.RecentAsync(10);
        Assert.Equal(2, log.Count);
        Assert.Contains(log, e => e is { Job: "jobs.good", Status: "Success" });
        var failed = Assert.Single(log, e => e.Status == "Failed");
        Assert.Equal("jobs.bad", failed.Job);
        Assert.Equal("it broke  Bcc: x", failed.Message);
        Assert.Single(await _engine.JobLog.RecentAsync(10, "Failed"));
        Assert.Single(await _engine.JobLog.RecentAsync(10, "Success"));
        Assert.Equal(2, (await _engine.JobLog.RecentAsync(10, "Anything else")).Count); // an unknown status filters nothing
    }

    [Fact]
    public async Task Long_text_is_cut_and_old_rows_are_removed_after_the_retention_period()
    {
        await _engine.JobLog.RecordAsync("old job", true, new string('x', 2000), TimeSpan.FromSeconds(2));
        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(40));
        await _engine.JobLog.RecordAsync("new job", true, null, TimeSpan.Zero);

        Assert.Equal(1, await _engine.JobLog.PurgeAsync(TimeSpan.FromDays(30)));

        var left = Assert.Single(await _engine.JobLog.RecentAsync(10));
        Assert.Equal("new job", left.Job);
        await _engine.JobLog.RecordAsync("clipped", true, new string('y', 2000), TimeSpan.FromMilliseconds(1500));
        var clipped = (await _engine.JobLog.RecentAsync(10)).Single(e => e.Job == "clipped");
        Assert.Equal(501, clipped.Message!.Length);
        Assert.Equal(1500, clipped.DurationMs);
    }

    [Fact]
    public async Task A_log_that_cannot_be_written_never_stops_the_job()
    {
        var bare = new JobLogService(new MetaRegistry(), _engine.Repository, _engine.History); // no Scheduled Job Log DocType here

        await bare.RecordAsync("job", false, "x", TimeSpan.Zero);

        Assert.Empty(await bare.RecentAsync(10));
        Assert.Equal(0, await bare.PurgeAsync(TimeSpan.Zero));
    }
}
