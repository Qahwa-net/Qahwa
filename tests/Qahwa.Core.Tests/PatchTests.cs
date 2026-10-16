using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class PatchTests
{
    private readonly TestEngine _engine = new();

    private sealed class Recording(string name, List<string> log, Func<Task>? body = null) : IPatch
    {
        public string Name => name;

        public async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
        {
            log.Add(name);
            if (body is not null)
                await body();
        }
    }

    private PatchRunner RunnerFor(params IPatch[] patches) => new(_engine.Registry, _engine.Repository, patches, new EmptyServices(), _engine.Clock);

    private async Task<Dictionary<string, object?>> RowAsync(string name) =>
        (await _engine.Documents.GetAsync("Patch Log", name, TestUsers.Admin));

    [Fact]
    public async Task Patches_run_once_in_name_order_and_are_recorded()
    {
        var log = new List<string>();
        var runner = RunnerFor(new Recording("2026_02_b", log), new Recording("2026_01_a", log), new Recording("2026_03_c", log));

        var first = await runner.RunPendingAsync();
        var second = await runner.RunPendingAsync();

        Assert.Equal(["2026_01_a", "2026_02_b", "2026_03_c"], log);
        Assert.Equal(log, first.Ran);
        Assert.Empty(second.Ran);
        Assert.Equal(3, second.Skipped.Count);
        var row = await RowAsync("2026_02_b");
        Assert.Equal("Done", row["status"]);
        Assert.NotNull(row["finished"]);
    }

    [Fact]
    public async Task A_new_patch_in_a_later_version_runs_alone()
    {
        var log = new List<string>();
        await RunnerFor(new Recording("p1", log)).RunPendingAsync();

        var result = await RunnerFor(new Recording("p1", log), new Recording("p2", log)).RunPendingAsync();

        Assert.Equal(["p1", "p2"], log);
        Assert.Equal(["p2"], result.Ran);
    }

    [Fact]
    public async Task A_failing_patch_stops_the_run_is_recorded_and_is_tried_again_next_time()
    {
        var log = new List<string>();
        var attempts = 0;
        IPatch flaky = new Recording("p2", log, () => ++attempts == 1 ? throw new InvalidOperationException("boom") : Task.CompletedTask);
        var runner = RunnerFor(new Recording("p1", log), flaky, new Recording("p3", log));

        var error = await Assert.ThrowsAsync<PatchFailedException>(() => runner.RunPendingAsync());

        Assert.Contains("p2", error.Message);
        Assert.Equal(["p1", "p2"], log); // p3 did not run: it may rely on p2
        var failed = await RowAsync("p2");
        Assert.Equal("Failed", failed["status"]);
        Assert.Contains("boom", (string)failed["error"]!);

        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var again = await runner.RunPendingAsync();
        Assert.Equal(["p2", "p3"], again.Ran);
        Assert.Equal("Done", (await RowAsync("p2"))["status"]);
        Assert.Null((await RowAsync("p2"))["error"]);
    }

    [Fact]
    public async Task Two_servers_starting_together_run_each_patch_only_once()
    {
        var log = new List<string>();
        var gate = new TaskCompletionSource();
        IPatch slow = new Recording("only-once", log, () => gate.Task);

        var first = RunnerFor(slow).RunPendingAsync();
        await Task.Delay(100);
        var second = await RunnerFor(slow).RunPendingAsync(); // sees it running

        Assert.Equal(["only-once"], log);
        Assert.Equal(["only-once"], second.Skipped);
        gate.SetResult();
        Assert.Equal(["only-once"], (await first).Ran);
    }

    [Fact]
    public async Task A_patch_whose_server_died_is_taken_over_after_a_while()
    {
        var log = new List<string>();
        var hang = new TaskCompletionSource();
        _ = RunnerFor(new Recording("stuck", log, () => hang.Task)).RunPendingAsync(); // never finishes
        await Task.Delay(100);
        _engine.Clock.Advance(PatchRunner.StaleAfter - TimeSpan.FromMinutes(1));
        Assert.Empty((await RunnerFor(new Recording("stuck", log)).RunPendingAsync()).Ran); // still looks alive

        _engine.Clock.Advance(TimeSpan.FromMinutes(2));
        var result = await RunnerFor(new Recording("stuck", log)).RunPendingAsync();

        Assert.Equal(["stuck"], result.Ran);
        Assert.Equal(2, log.Count);
        hang.SetResult();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Has Capitals")]
    [InlineData("../escape")]
    [InlineData("-leading")]
    [InlineData("name with spaces")]
    public void Bad_names_are_found_when_the_server_starts(string name)
    {
        var runner = RunnerFor(new Recording(name, []));

        Assert.Throws<InvalidOperationException>(runner.Validate);
        Assert.False(PatchRunner.ValidName(name));
    }

    [Fact]
    public void The_same_name_twice_is_found_when_the_server_starts() =>
        Assert.Throws<InvalidOperationException>(RunnerFor(new Recording("a", []), new Recording("a", [])).Validate);

    [Fact]
    public async Task The_log_can_be_read_but_not_edited_by_a_system_manager()
    {
        await RunnerFor(new Recording("p1", [])).RunPendingAsync();

        Assert.Equal("Done", (await _engine.Documents.GetAsync("Patch Log", "p1", TestUsers.SystemManager))["status"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Patch Log", TestEngine.Values(("patch", "fake")), TestUsers.SystemManager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Patch Log", "p1", TestEngine.Values(("status", "Failed")), TestUsers.SystemManager));
    }
}
