using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Imports of big files that run in the background as the person who asked.</summary>
public class ImportRunTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Reader = new("rae@example.com", ["Reader"]);
    private static readonly UserContext Boss = new("boss@example.com", ["System Manager"]);

    public ImportRunTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Lead", "autoname": "LD-.#####", "fields": [ { "name": "full_name", "type": "Data", "required": true }, { "name": "age", "type": "Int" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "import": true }, { "role": "Reader", "read": true } ] }
            """));
    }

    private async Task PeopleAsync()
    {
        await _engine.Auth.EnsureUserAsync(Ann.User, "Ann", TestUsers.StrongPassword, ["Clerk"]);
        await _engine.Auth.EnsureUserAsync(Boss.User, "Boss", TestUsers.StrongPassword, ["System Manager"]);
        await _engine.Auth.EnsureUserAsync(Reader.User, "Rae", TestUsers.StrongPassword, ["Reader"]);
    }

    private static string File(int rows, Func<int, string>? row = null) =>
        "full_name,age\r\n" + string.Concat(Enumerable.Range(1, rows).Select(i => (row?.Invoke(i) ?? $"Person {i},{i}") + "\r\n"));

    private async Task<long> LeadsAsync() =>
        (await _engine.Documents.ListAsync("Lead", new ListRequest { Limit = 1 }, TestUsers.Admin)).Total;

    [Fact]
    public async Task A_big_file_is_checked_now_queued_and_imported_later_as_the_person()
    {
        await PeopleAsync();
        var started = await _engine.ImportRuns.StartAsync("Lead", File(60), "Insert", false, Ann);

        Assert.Equal(("Queued", 60), (started.Status, started.Rows));
        Assert.Equal(0, await LeadsAsync()); // nothing yet

        Assert.Equal((1, 0), await _engine.ImportRuns.RunDueAsync());
        var done = await _engine.ImportRuns.GetAsync(started.Id, Ann);

        Assert.Equal(("Done", 60, 60, 0), (done.Status, done.Processed, done.Created, done.Failed));
        Assert.Equal(60, await LeadsAsync());
        var created = (await _engine.Documents.ListAsync("Lead", new ListRequest { Limit = 1, Fields = ["owner"] }, TestUsers.Admin)).Data.Single();
        Assert.Equal(Ann.User, created["owner"]); // made by the person, not by the system

        var stored = await _engine.Repository.GetAsync(_engine.Registry.Get("Import Run"), started.Id);
        Assert.Null(stored!["csv"]); // the file is thrown away when the run ends
    }

    [Fact]
    public async Task A_bad_file_or_missing_rights_are_refused_when_asking_not_hours_later()
    {
        await PeopleAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ImportRuns.StartAsync("Lead", File(3), "Insert", false, Reader));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ImportRuns.StartAsync("Lead", "nonsense,columns\r\n1,2\r\n", "Insert", false, Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ImportRuns.StartAsync("Lead", File(DataImportService.LargeMaxRows + 1), "Insert", false, Ann));
        Assert.Empty(await _engine.ImportRuns.ListAsync(Ann));
    }

    [Fact]
    public async Task Failed_rows_are_reported_with_their_lines_and_a_person_has_at_most_two_waiting()
    {
        await PeopleAsync();
        var run = await _engine.ImportRuns.StartAsync("Lead", File(30, i => i == 7 ? "Bad,notanumber" : $"P{i},{i}"), "Insert", false, Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1)); // the first one is the oldest
        await _engine.ImportRuns.StartAsync("Lead", File(2), "Insert", false, Ann);
        await Assert.ThrowsAsync<ConflictException>(() => _engine.ImportRuns.StartAsync("Lead", File(2), "Insert", false, Ann));

        await _engine.ImportRuns.RunDueAsync();
        var done = await _engine.ImportRuns.GetAsync(run.Id, Ann);
        Assert.Equal((29, 1), (done.Created, done.Failed));
        Assert.Equal(8, Assert.Single(done.Errors).Row); // line 8 of the file: header plus seven rows
        await _engine.ImportRuns.StartAsync("Lead", File(2), "Insert", false, Ann); // a place is free again
    }

    [Fact]
    public async Task Only_the_owner_or_a_manager_can_look_and_a_waiting_run_can_be_cancelled()
    {
        await PeopleAsync();
        var run = await _engine.ImportRuns.StartAsync("Lead", File(5), "Insert", false, Ann);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ImportRuns.GetAsync(run.Id, Reader));
        Assert.Equal(Ann.User, (await _engine.ImportRuns.GetAsync(run.Id, Boss)).User);
        Assert.Empty(await _engine.ImportRuns.ListAsync(Reader));
        Assert.Single(await _engine.ImportRuns.ListAsync(Boss, everyone: true));

        await _engine.ImportRuns.CancelAsync(run.Id, Ann);
        Assert.Equal((0, 0), await _engine.ImportRuns.RunDueAsync());
        Assert.Equal("Cancelled", (await _engine.ImportRuns.GetAsync(run.Id, Ann)).Status);
        Assert.Equal(0, await LeadsAsync());
        await Assert.ThrowsAsync<ConflictException>(() => _engine.ImportRuns.CancelAsync(run.Id, Ann)); // already over
    }

    [Fact]
    public async Task A_stop_request_is_honoured_after_the_next_batch_and_what_was_imported_stays()
    {
        await PeopleAsync();
        var run = await _engine.ImportRuns.StartAsync("Lead", File(100), "Insert", false, Ann);
        var meta = _engine.Registry.Get("Import Run");
        await _engine.Repository.UpdateAsync(meta, run.Id, new Dictionary<string, object?> { ["cancel_requested"] = true }, null);

        await _engine.ImportRuns.RunDueAsync();

        var stopped = await _engine.ImportRuns.GetAsync(run.Id, Ann);
        Assert.Equal("Cancelled", stopped.Status);
        Assert.Equal(DataImportService.ProgressEvery, stopped.Created);
        Assert.Equal(DataImportService.ProgressEvery, await LeadsAsync());
    }

    [Fact]
    public async Task A_person_who_is_disabled_before_the_run_gets_a_clear_failure_and_nothing_is_imported()
    {
        await PeopleAsync();
        var run = await _engine.ImportRuns.StartAsync("Lead", File(5), "Insert", false, Ann);
        await _engine.Documents.UpdateAsync("User", Ann.User, Values(("enabled", false)), TestUsers.Admin);

        Assert.Equal((0, 1), await _engine.ImportRuns.RunDueAsync());

        var failed = await _engine.ImportRuns.GetAsync(run.Id, Boss);
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("not an enabled user", failed.Message);
        Assert.Equal(0, await LeadsAsync());
    }

    [Fact]
    public async Task A_run_that_stopped_reporting_is_marked_failed_and_never_run_again_and_old_runs_are_removed()
    {
        await PeopleAsync();
        var run = await _engine.ImportRuns.StartAsync("Lead", File(5), "Insert", false, Ann);
        var meta = _engine.Registry.Get("Import Run");
        var row = (await _engine.Repository.GetAsync(meta, run.Id))!;
        await _engine.Repository.UpdateAsync(meta, run.Id, new Dictionary<string, object?> { ["status"] = "Running" }, null); // a crash left it Running

        _engine.Clock.Advance(ImportRunService.StaleAfter + TimeSpan.FromMinutes(1));
        Assert.Equal((0, 1), await _engine.ImportRuns.RunDueAsync());
        var failed = await _engine.ImportRuns.GetAsync(run.Id, Ann);
        Assert.Equal("Failed", failed.Status);
        Assert.Contains("not run again", failed.Message);
        Assert.Equal(0, await LeadsAsync());
        Assert.NotNull(row);

        _engine.Clock.Advance(ImportRunService.KeepFor + TimeSpan.FromDays(1));
        Assert.Equal(1, await _engine.ImportRuns.PurgeAsync());
        Assert.Empty(await _engine.ImportRuns.ListAsync(Ann));
    }
}
