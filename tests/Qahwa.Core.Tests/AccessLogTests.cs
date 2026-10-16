using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AccessLogTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public AccessLogTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ledger", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "export": true }, { "role": "Viewer", "read": true } ] }
            """));
    }

    private async Task<List<Dictionary<string, object?>>> LogAsync() =>
        (await _engine.Repository.ListAsync(_engine.Registry.Get("Access Log"), new ListQuery { Limit = 50, OrderBy = "creation", Descending = false })).ToList();

    [Fact]
    public async Task Exports_and_printouts_are_recorded_with_who_what_and_how_many()
    {
        await _engine.Documents.InsertAsync("Ledger", Values(("title", "a")), Ann);
        await _engine.Documents.InsertAsync("Ledger", Values(("title", "b")), Ann);

        await _engine.Documents.ExportAsync("Ledger", new ListRequest { Limit = 100 }, Ann);
        await _engine.Print.RenderAsync("Ledger", "a", null, null, Vic);

        var log = await LogAsync();
        Assert.Equal(2, log.Count);
        var export = log.Single(l => (string)l["action"]! == "Export");
        var print = log.Single(l => (string)l["action"]! == "Print");
        Assert.Equal(("Ledger", Ann.User, 2L), ((string)export["ref_doctype"]!, (string)export["user"]!, Convert.ToInt64(export["rows"])));
        Assert.Equal(("a", Vic.User), ((string)print["ref_docname"]!, (string)print["user"]!));
    }

    [Fact]
    public async Task A_refused_export_or_print_leaves_no_line_and_old_lines_are_purged()
    {
        await _engine.Documents.InsertAsync("Ledger", Values(("title", "a")), Ann);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ExportAsync("Ledger", new ListRequest(), Vic));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.RenderAsync("Ledger", "ghost", null, null, Ann));
        Assert.Empty(await LogAsync());

        await _engine.Documents.ExportAsync("Ledger", new ListRequest(), Ann);
        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(400));
        await _engine.Documents.ExportAsync("Ledger", new ListRequest(), Ann);
        Assert.Equal(1, await _engine.AccessLog.PurgeAsync(TimeSpan.FromDays(365)));
        Assert.Single(await LogAsync());
    }

    [Fact]
    public async Task Only_system_managers_can_read_the_log_and_nobody_can_forge_a_line()
    {
        var clerkRead = await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Access Log", new ListRequest(), Ann));
        Assert.NotNull(clerkRead);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Access Log", Values(("user", "x"), ("action", "Export")), TestUsers.SystemManager));
    }
}
