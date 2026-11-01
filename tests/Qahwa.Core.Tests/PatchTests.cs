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
    public async Task The_list_shows_each_registered_patch_with_its_state_and_logged_ones_that_are_gone()
    {
        var log = new List<string>();
        await RunnerFor(new Recording("old_one", log), new Recording("kept", log)).RunPendingAsync();
        var runner = RunnerFor(new Recording("kept", log), new Recording("new_one", log));

        var list = await runner.ListAsync();

        Assert.Equal([("kept", "Done"), ("new_one", "Pending"), ("old_one", "Done")], list.Select(p => (p.Name, p.Status)));
        Assert.Null(list.Single(p => p.Name == "new_one").Started);
        Assert.NotNull(list.Single(p => p.Name == "kept").Finished);
    }

    [Fact]
    public async Task One_patch_can_be_run_by_name_and_a_done_one_only_when_forced()
    {
        var log = new List<string>();
        var runner = RunnerFor(new Recording("a", log), new Recording("b", log));

        var ran = await runner.RunOneAsync("b", force: false);
        Assert.Equal(("b", "Done"), (ran.Name, ran.Status));
        Assert.Equal(["b"], log); // only that one

        await Assert.ThrowsAsync<ConflictException>(() => runner.RunOneAsync("b", force: false));
        await runner.RunOneAsync("b", force: true);
        Assert.Equal(["b", "b"], log);
        await Assert.ThrowsAsync<NotFoundException>(() => runner.RunOneAsync("nope", force: true));
        Assert.Equal("Pending", (await runner.ListAsync()).Single(p => p.Name == "a").Status);
    }

    [Fact]
    public async Task A_failing_patch_run_by_name_is_recorded_and_can_be_run_again_without_force()
    {
        var attempts = 0;
        var runner = RunnerFor(new Recording("flaky", [], () => ++attempts == 1 ? throw new InvalidOperationException("broken") : Task.CompletedTask));

        await Assert.ThrowsAsync<PatchFailedException>(() => runner.RunOneAsync("flaky", force: false));
        var failed = (await runner.ListAsync()).Single();
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("broken", failed.Error);

        Assert.Equal("Done", (await runner.RunOneAsync("flaky", force: false)).Status); // a failed one needs no force
    }

    [Fact]
    public async Task A_patch_another_server_is_running_is_refused_even_when_forced()
    {
        var runner = RunnerFor(new Recording("busy", []));
        await runner.RunOneAsync("busy", force: false);
        var row = (await _engine.Repository.GetAsync(_engine.Registry.Get("Patch Log"), "busy"))!;
        await _engine.Repository.UpdateAsync(_engine.Registry.Get("Patch Log"), "busy", new Dictionary<string, object?> { ["status"] = "Running", ["started"] = _engine.Clock.GetUtcNow().UtcDateTime }, null);
        Assert.NotNull(row);

        await Assert.ThrowsAsync<ConflictException>(() => runner.RunOneAsync("busy", force: true));
    }

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

    private sealed class PatchedApp(string name, string[] needs, params IPatch[] patches) : IQahwaApp
    {
        public string Name => name;
        public string Version => "1.0.0";
        public IReadOnlyList<string> Dependencies => needs;
        public IReadOnlyList<IPatch> Patches => patches;
        public void Configure(IAppBuilder app) { }
    }

    [Fact]
    public void An_apps_patches_run_after_those_of_the_apps_it_needs_and_host_patches_run_first()
    {
        var log = new List<string>();
        IPatch P(string name) => new Recording(name, log);
        var core = new PatchedApp("core_pack", [], P("core_pack_2026_03"), P("core_pack_2026_01"));
        var shop = new PatchedApp("shop", ["core_pack"], P("shop_2026_01"));
        var host = P("zz_host_patch");
        var all = new[] { shop.Patches[0], core.Patches[0], host, core.Patches[1] };

        var order = PatchOrder.Sort(all, [shop, core]).Select(p => p.Name);

        // The shop's patch is named first alphabetically but needs core_pack's, so it comes after both of them.
        Assert.Equal(["zz_host_patch", "core_pack_2026_01", "core_pack_2026_03", "shop_2026_01"], order);
    }

    [Fact]
    public void Ordering_never_fails_on_apps_that_cannot_be_ordered()
    {
        var log = new List<string>();
        var a = new PatchedApp("aa", ["missing"], new Recording("aa_1", log));
        var b = new PatchedApp("bb", [], new Recording("bb_1", log));

        Assert.Equal(["aa_1", "bb_1"], PatchOrder.Sort([b.Patches[0], a.Patches[0]], [b, a]).Select(p => p.Name));
    }

    [Fact]
    public async Task The_runner_runs_app_patches_in_that_order()
    {
        var log = new List<string>();
        var core = new PatchedApp("core_pack", [], new Recording("core_pack_1", log));
        var shop = new PatchedApp("shop", ["core_pack"], new Recording("a_shop_first", log));
        var runner = new PatchRunner(_engine.Registry, _engine.Repository, [shop.Patches[0], core.Patches[0]], new EmptyServices(), _engine.Clock, [shop, core]);

        await runner.RunPendingAsync();

        Assert.Equal(["core_pack_1", "a_shop_first"], log);
        Assert.Equal(["core_pack_1", "a_shop_first"], (await runner.ListAsync()).Select(p => p.Name));
    }
}
