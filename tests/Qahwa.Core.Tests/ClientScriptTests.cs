using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ClientScriptTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Visitor"]);

    private readonly ClientScriptService _service;

    public ClientScriptTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "autoname": "TK-.####", "fields": [ { "name": "status", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "autoname": "ME-.####", "fields": [ { "name": "text", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true } ] }
            """));
        _service = new ClientScriptService(_engine.Registry, _engine.Repository, _engine.Permissions);
    }

    private async Task<string> ScriptAsync(string source, string doctype = "Ticket", bool active = true, string title = "S")
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        return (string)(await _engine.Documents.InsertAsync("Client Script", Values(("title", title), ("active", active ? 1 : 0), ("reference_doctype", doctype), ("script", source)), TestUsers.SystemManager))["name"]!;
    }

    [Fact]
    public async Task The_scripts_of_a_doctype_go_to_people_who_may_open_it_in_order_and_only_the_active_ones()
    {
        await ScriptAsync("// one", title: "One");
        await ScriptAsync("// off", active: false, title: "Off");
        await ScriptAsync("// two", title: "Two");
        await ScriptAsync("// memo", "Memo", title: "Memo");

        var scripts = await _service.ForAsync("Ticket", Ann);

        Assert.Equal(["One", "Two"], scripts.Select(s => s.Title));
        Assert.Equal("// one", scripts[0].Source);
        Assert.Equal(["Memo"], (await _service.ForAsync("Memo", Ann)).Select(s => s.Title));
    }

    [Fact]
    public async Task Someone_who_cannot_open_the_doctype_learns_nothing_about_its_scripts()
    {
        await ScriptAsync("// secret logic");

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _service.ForAsync("Ticket", Vic));
        await Assert.ThrowsAsync<NotFoundException>(() => _service.ForAsync("No Such Type", Ann));
    }

    [Fact]
    public async Task Mistakes_and_oversized_sources_are_refused_when_saved()
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Client Script", Values(("title", "Bad"), ("active", 1), ("reference_doctype", "Ticket"), ("script", "if (")), TestUsers.SystemManager));
        Assert.Equal("script", error.Errors.Single().Field);
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Client Script", Values(("title", "Big"), ("active", 1), ("reference_doctype", "Ticket"), ("script", new string(' ', 20_001) + "1;")), TestUsers.SystemManager));
    }

    [Fact]
    public async Task Only_a_system_manager_can_make_or_list_scripts()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.InsertAsync("Client Script", Values(("title", "Mine"), ("active", 1), ("reference_doctype", "Ticket"), ("script", "1;")), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Client Script", new ListRequest(), Ann));
    }

    [Fact]
    public async Task At_most_ten_scripts_and_no_oversized_ones_are_sent()
    {
        for (var i = 0; i < 12; i++)
            await ScriptAsync($"// {i}", title: $"S{i}");

        Assert.Equal(ClientScriptService.MaxPerDocType, (await _service.ForAsync("Ticket", Ann)).Count);
    }
}
