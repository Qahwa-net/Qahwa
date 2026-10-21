using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ScheduledScriptTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Maker = new("maker@example.com", ["Clerk", "System Manager"]);

    public ScheduledScriptTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Task Log", "autoname": "TL-.####", "fields": [ { "name": "note", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task<string> ScriptAsync(string source, string frequency = "Daily", UserContext? by = null, bool active = true)
    {
        var created = await _engine.Documents.InsertAsync("Server Script", Values(("title", "Job"), ("active", active ? 1 : 0), ("event", "Scheduled"), ("frequency", frequency), ("script", source)), by ?? TestUsers.SystemManager);
        return (string)created["name"]!;
    }

    private Task<(int Ran, int Failed)> RunAsync(UserContext? as_ = null) =>
        _engine.ServerScripts.RunScheduledAsync((_, _) => Task.FromResult<IUserContext?>(as_ ?? TestUsers.SystemManager), default);

    private async Task<int> LogsAsync() =>
        (await _engine.Documents.ListAsync("Task Log", new ListRequest { Fields = ["name"], Limit = 100 }, TestUsers.Admin)).Data.Count;

    [Fact]
    public async Task A_new_script_is_scheduled_first_and_runs_when_its_time_comes_once_per_period()
    {
        await ScriptAsync("qahwa.insert('Task Log', { note: 'ran' });", "Hourly", Maker);
        Assert.Equal((0, 0), await RunAsync(Maker)); // first sight: only scheduled
        Assert.Equal(0, await LogsAsync());

        _engine.Clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal((0, 0), await RunAsync(Maker)); // not yet
        _engine.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal((1, 0), await RunAsync(Maker));
        Assert.Equal(1, await LogsAsync());
        Assert.Equal((0, 0), await RunAsync(Maker)); // the same slot is not run twice
        Assert.Equal(1, await LogsAsync());

        _engine.Clock.Advance(TimeSpan.FromHours(3)); // missed runs happen once, not three times
        Assert.Equal((1, 0), await RunAsync(Maker));
        Assert.Equal(2, await LogsAsync());
    }

    [Fact]
    public async Task A_daily_script_waits_a_day()
    {
        await ScriptAsync("qahwa.insert('Task Log', { note: 'd' });", "Daily", Maker);
        await RunAsync(Maker);
        _engine.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal((0, 0), await RunAsync(Maker));
        _engine.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal((1, 0), await RunAsync(Maker));
    }

    [Fact]
    public async Task A_script_runs_with_the_permissions_of_the_person_who_made_it()
    {
        var weak = new UserContext("weak@example.com", ["Viewer", "System Manager"]);
        var name = await ScriptAsync("qahwa.insert('Task Log', { note: 'x' });", "Hourly", weak);
        await RunAsync(weak);
        _engine.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal((0, 1), await RunAsync(weak)); // a Viewer may not create Task Logs
        Assert.Equal(0, await LogsAsync());
        Assert.Contains("permission", (string)(await _engine.Documents.GetAsync("Server Script", name, TestUsers.Admin))["last_error"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_script_whose_maker_is_gone_is_not_run_and_says_why()
    {
        var name = await ScriptAsync("qahwa.insert('Task Log', { note: 'x' });", "Hourly", Maker);
        await _engine.ServerScripts.RunScheduledAsync((_, _) => Task.FromResult<IUserContext?>(Maker), default);
        _engine.Clock.Advance(TimeSpan.FromHours(2));
        var result = await _engine.ServerScripts.RunScheduledAsync((_, _) => Task.FromResult<IUserContext?>(null), default);
        Assert.Equal((0, 1), result);
        Assert.Equal(0, await LogsAsync());
        Assert.Contains("not an enabled user", (string)(await _engine.Documents.GetAsync("Server Script", name, TestUsers.Admin))["last_error"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switched_off_scripts_and_runaway_scripts_are_handled()
    {
        await ScriptAsync("qahwa.insert('Task Log', { note: 'off' });", "Hourly", Maker, active: false);
        await ScriptAsync("while (true) {}", "Hourly", Maker);
        await RunAsync(Maker);
        _engine.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal((0, 1), await RunAsync(Maker)); // the loop is stopped; the switched-off one never runs
        Assert.Equal(0, await LogsAsync());
    }

    [Fact]
    public async Task Only_hourly_or_daily_and_only_system_managers_make_them()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => ScriptAsync("1;", "Every second"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => ScriptAsync("1;", "Daily", new UserContext("c@example.com", ["Clerk"])));
    }
}
