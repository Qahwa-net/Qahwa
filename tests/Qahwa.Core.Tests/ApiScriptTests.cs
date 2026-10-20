using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ApiScriptTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public ApiScriptTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Parcel", "autoname": "PA-.####", "fields": [ { "name": "weight", "type": "Float" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Viewer", "read": true } ] }
            """));
    }

    private async Task ApiAsync(string method, string source, string? role = null, bool active = true)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Server Script", Values(("title", method), ("active", active ? 1 : 0), ("event", "API"), ("method_name", method), ("allowed_role", role), ("script", source)), TestUsers.SystemManager);
    }

    [Fact]
    public async Task A_signed_in_person_calls_a_script_by_name_and_gets_its_result()
    {
        await ApiAsync("double_it", "result = { twice: doc.n * 2, who: user.name };");
        var json = await _engine.ServerScripts.CallMethodAsync("double_it", new Dictionary<string, object?> { ["n"] = 21 }, Ann, default);
        Assert.Equal("""{"twice":42,"who":"ann@example.com"}""", json);
    }

    [Fact]
    public async Task What_a_script_reads_and_writes_follows_the_callers_permissions()
    {
        await ApiAsync("add_parcel", "result = qahwa.insert('Parcel', { weight: doc.w });");
        var made = await _engine.ServerScripts.CallMethodAsync("add_parcel", new Dictionary<string, object?> { ["w"] = 3 }, Ann, default);
        Assert.StartsWith("\"PA-", made, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ServerScripts.CallMethodAsync("add_parcel", new Dictionary<string, object?> { ["w"] = 3 }, Vic, default));
    }

    [Fact]
    public async Task A_role_can_limit_who_may_call_and_unknown_or_switched_off_methods_are_not_found()
    {
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Dispatcher")), TestUsers.Admin);
        await ApiAsync("only_dispatch", "result = 1;", role: "Dispatcher");
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ServerScripts.CallMethodAsync("only_dispatch", new Dictionary<string, object?>(), Ann, default));
        Assert.Equal("1", await _engine.ServerScripts.CallMethodAsync("only_dispatch", new Dictionary<string, object?>(), new UserContext("d@example.com", ["Dispatcher"]), default));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ServerScripts.CallMethodAsync("nope", new Dictionary<string, object?>(), Ann, default));
        await ApiAsync("off", "result = 1;", active: false);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ServerScripts.CallMethodAsync("off", new Dictionary<string, object?>(), Ann, default));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.ServerScripts.CallMethodAsync("only_dispatch", new Dictionary<string, object?>(), UserContext.Anonymous, default));
    }

    [Fact]
    public async Task A_runaway_or_failing_script_is_stopped_with_a_message()
    {
        await ApiAsync("loop", "while (true) {}");
        await ApiAsync("boom", "qahwa.error('No parcels today');");
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ServerScripts.CallMethodAsync("loop", new Dictionary<string, object?>(), Ann, default));
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ServerScripts.CallMethodAsync("boom", new Dictionary<string, object?>(), Ann, default));
        Assert.Contains("No parcels today", ex.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bad Name")]
    [InlineData("1abc")]
    [InlineData("a-b")]
    [InlineData("x; drop")]
    public async Task A_method_name_must_be_plain(string name) =>
        await Assert.ThrowsAsync<DocumentValidationException>(() => ApiAsync(name, "result = 1;"));

    [Fact]
    public async Task Other_scripts_still_need_a_DocType()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Server Script", Values(("title", "x"), ("event", "Validate"), ("script", "1;")), TestUsers.SystemManager));
    }
}
