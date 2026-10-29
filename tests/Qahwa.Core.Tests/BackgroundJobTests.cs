using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class BackgroundJobTests
{
    private readonly TestEngine _engine = new();
    private readonly List<string> _seen = [];
    private int _calls;

    private sealed class MailApp(Action<IAppBuilder> configure) : IQahwaApp
    {
        public string Name => "mailer";
        public string Version => "1.0.0";
        public void Configure(IAppBuilder app) => configure(app);
    }

    private void Install(Action<IAppBuilder> configure) => _engine.AppCatalog.Add(new MailApp(configure));

    private async Task<Dictionary<string, object?>> JobAsync(string name) => await _engine.Documents.GetAsync("Background Job", name, TestUsers.Admin);

    [Fact]
    public async Task Queued_work_runs_with_its_data_and_is_marked_done_once()
    {
        Install(a => a.Queue("greet", ctx =>
        {
            _seen.Add(ctx.Payload.GetProperty("who").GetString() + "#" + ctx.Attempt);
            return Task.CompletedTask;
        }));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.greet", new { who = "Ann" });

        Assert.Equal(new QueueRun(1, 0, 0), await _engine.BackgroundJobs.RunDueAsync());
        Assert.Equal(["Ann#1"], _seen);
        var row = await JobAsync(job);
        Assert.Equal("Done", row["status"]);
        Assert.Equal(1, Convert.ToInt32(row["attempts"]));
        Assert.Equal(new QueueRun(0, 0, 0), await _engine.BackgroundJobs.RunDueAsync()); // not run twice
    }

    [Fact]
    public async Task A_delayed_job_waits_for_its_time()
    {
        Install(a => a.Queue("later", _ => { _calls++; return Task.CompletedTask; }));
        await _engine.BackgroundJobs.EnqueueAsync("mailer.later", delay: TimeSpan.FromMinutes(10));
        await _engine.BackgroundJobs.RunDueAsync();
        Assert.Equal(0, _calls);
        _engine.Clock.Advance(TimeSpan.FromMinutes(11));
        await _engine.BackgroundJobs.RunDueAsync();
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task A_failure_is_tried_again_after_a_growing_pause_and_then_fails_for_good()
    {
        Install(a => a.Queue("flaky", _ => { _calls++; throw new InvalidOperationException("boom\r\nline two"); }));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.flaky", maxAttempts: 3);

        Assert.Equal(new QueueRun(0, 0, 1), await _engine.BackgroundJobs.RunDueAsync());
        var first = await JobAsync(job);
        Assert.Equal("Queued", first["status"]);
        Assert.Equal("boom  line two", first["last_error"]); // control characters are removed
        Assert.Equal(new QueueRun(0, 0, 0), await _engine.BackgroundJobs.RunDueAsync()); // still waiting

        _engine.Clock.Advance(BackgroundJobService.Backoff(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(new QueueRun(0, 0, 1), await _engine.BackgroundJobs.RunDueAsync());
        _engine.Clock.Advance(BackgroundJobService.Backoff(2) + TimeSpan.FromSeconds(1));
        Assert.Equal(new QueueRun(0, 1, 0), await _engine.BackgroundJobs.RunDueAsync());

        var last = await JobAsync(job);
        Assert.Equal("Failed", last["status"]);
        Assert.Equal(3, Convert.ToInt32(last["attempts"]));
        Assert.Equal(3, _calls);
        _engine.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(new QueueRun(0, 0, 0), await _engine.BackgroundJobs.RunDueAsync());
    }

    [Fact]
    public async Task A_failed_job_can_be_retried_by_a_manager_and_only_a_failed_one()
    {
        var works = false;
        Install(a => a.Queue("mend", _ => works ? Task.CompletedTask : throw new InvalidOperationException("not yet")));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.mend", maxAttempts: 1);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.BackgroundJobs.RetryAsync(job)); // still queued
        await _engine.BackgroundJobs.RunDueAsync();
        Assert.Equal("Failed", (await JobAsync(job))["status"]);

        works = true;
        await _engine.BackgroundJobs.RetryAsync(job);
        Assert.Equal(new QueueRun(1, 0, 0), await _engine.BackgroundJobs.RunDueAsync());
        Assert.Equal("Done", (await JobAsync(job))["status"]);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.BackgroundJobs.RetryAsync("missing"));
    }

    [Fact]
    public async Task A_job_that_runs_past_its_queue_limit_is_stopped()
    {
        Install(a => a.Queue("slow", async ctx => await Task.Delay(TimeSpan.FromSeconds(60), ctx.Cancellation)));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.slow", queue: "short", maxAttempts: 1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(new QueueRun(0, 1, 0), await _engine.BackgroundJobs.RunDueAsync());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(45), watch.Elapsed.ToString());
        Assert.Contains("longer than the short queue allows", (string)(await JobAsync(job))["last_error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_job_left_running_by_a_crashed_server_is_queued_again()
    {
        Install(a => a.Queue("again", _ => { _calls++; return Task.CompletedTask; }));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.again", queue: "short");
        var meta = _engine.Registry.Get("Background Job");
        var row = (await _engine.Repository.GetAsync(meta, job))!;
        await _engine.Repository.UpdateAsync(meta, job, new Dictionary<string, object?> { ["status"] = "Running", ["started_at"] = _engine.Clock.GetUtcNow().UtcDateTime, ["modified"] = _engine.Clock.GetUtcNow().UtcDateTime.AddTicks(10) }, (DateTime)row["modified"]!);

        Assert.Equal(new QueueRun(0, 0, 0), await _engine.BackgroundJobs.RunDueAsync()); // recently started: left alone
        _engine.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(new QueueRun(1, 0, 0), await _engine.BackgroundJobs.RunDueAsync());
        Assert.Equal(1, _calls);
    }

    [Fact]
    public async Task Bad_requests_to_queue_work_are_refused()
    {
        Install(a => a.Queue("ok", _ => Task.CompletedTask));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.BackgroundJobs.EnqueueAsync("mailer.nope"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.BackgroundJobs.EnqueueAsync("mailer.ok", queue: "huge"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.BackgroundJobs.EnqueueAsync("mailer.ok", maxAttempts: 11));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.BackgroundJobs.EnqueueAsync("mailer.ok", delay: TimeSpan.FromDays(31)));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.BackgroundJobs.EnqueueAsync("mailer.ok", new { big = new string('x', BackgroundJobService.MaxPayloadBytes) }));
    }

    [Fact]
    public async Task Handlers_of_a_switched_off_app_do_not_run_and_the_job_fails_clearly()
    {
        Install(a => a.Queue("off", _ => { _calls++; return Task.CompletedTask; }));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.off", maxAttempts: 1);
        _engine.AppCatalog.SetDisabled(["mailer"]);
        await _engine.BackgroundJobs.RunDueAsync();
        Assert.Equal(0, _calls);
        Assert.Equal("Failed", (await JobAsync(job))["status"]);
        Assert.Contains("not installed", (string)(await JobAsync(job))["last_error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Short_work_goes_first_and_old_finished_jobs_are_removed()
    {
        var order = new List<string>();
        Install(a => a.Queue("note", ctx => { order.Add(ctx.Payload.GetString()!); return Task.CompletedTask; }));
        await _engine.BackgroundJobs.EnqueueAsync("mailer.note", "long-one", queue: "long");
        await _engine.BackgroundJobs.EnqueueAsync("mailer.note", "short-one", queue: "short");
        await _engine.BackgroundJobs.RunDueAsync();
        Assert.Equal(["short-one", "long-one"], order);

        _engine.Clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(2, await _engine.BackgroundJobs.PurgeAsync());
        Assert.Empty((await _engine.Documents.ListAsync("Background Job", new ListRequest { Fields = ["name"], Limit = 10 }, TestUsers.Admin)).Data);
    }

    [Fact]
    public void Handler_names_are_checked_when_an_app_is_added()
    {
        Assert.Throws<InvalidOperationException>(() => Install(a => a.Queue("Bad Name", _ => Task.CompletedTask)));
        var catalog = new AppCatalog([]);
        Assert.Throws<InvalidOperationException>(() => catalog.Add(new MailApp(a => a.Queue("twice", _ => Task.CompletedTask).Queue("twice", _ => Task.CompletedTask))));
    }

    [Fact]
    public async Task Only_system_managers_can_read_jobs_and_nobody_can_edit_them_by_hand()
    {
        Install(a => a.Queue("x", _ => Task.CompletedTask));
        var job = await _engine.BackgroundJobs.EnqueueAsync("mailer.x");
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Background Job", job, new UserContext("c@example.com", ["Clerk"])));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Background Job", job, Values(("status", "Done")), TestUsers.SystemManager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Background Job", Values(("handler", "mailer.x")), TestUsers.SystemManager));
    }
}
