using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AppPermissionTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);

    public AppPermissionTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "isSubmittable": true, "autoname": "TK-.####",
              "fields": [ { "name": "title", "type": "Data" }, { "name": "region", "type": "Data" }, { "name": "secret", "type": "Password" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true, "submit": true, "cancel": true } ] }
            """));
    }

    private sealed class RegionApp(Action<IAppBuilder> configure) : IQahwaApp
    {
        public string Name => "regions";
        public string Version => "1.0.0";
        public void Configure(IAppBuilder app) => configure(app);
    }

    private void Install(Action<IAppBuilder> configure) => _engine.AppCatalog.Add(new RegionApp(configure));

    private static string RegionOf(IUserContext user) => user.User == "ann@example.com" ? "north" : "south";

    private Task<Dictionary<string, object?>> TicketAsync(string region, UserContext? by = null) =>
        _engine.Documents.InsertAsync("Ticket", Values(("title", $"in {region}"), ("region", region)), by ?? TestUsers.Admin);

    private async Task<string[]> RegionsSeenAsync(UserContext user, params RawFilter[] filters) =>
        (await _engine.Documents.ListAsync("Ticket", new ListRequest { Fields = ["region"], OrderBy = "name", Descending = false, Filters = filters }, user)).Data.Select(r => (string)r["region"]!).ToArray();

    [Fact]
    public async Task An_app_check_hides_documents_from_people_it_refuses_and_a_query_filters_their_lists()
    {
        Install(a => a
            .HasPermission("Ticket", ctx => (string?)ctx.Doc.GetValueOrDefault("region") == RegionOf(ctx.User))
            .PermissionQuery("Ticket", user => [new PermissionFilter("region", [RegionOf(user)])]));
        var north = await TicketAsync("north");
        var south = await TicketAsync("south");

        Assert.Equal(["north"], await RegionsSeenAsync(Ann));
        Assert.Equal(["south"], await RegionsSeenAsync(Bob));
        Assert.Equal(1, (await _engine.Documents.ListAsync("Ticket", new ListRequest { Limit = 1 }, Ann)).Total);
        Assert.Equal("north", (await _engine.Documents.GetAsync("Ticket", (string)north["name"]!, Ann))["region"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Ticket", (string)south["name"]!, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Ticket", (string)south["name"]!, Values(("title", "x")), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Ticket", (string)south["name"]!, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.SubmitAsync("Ticket", (string)south["name"]!, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => TicketAsync("south", Ann));
        Assert.Equal("north", (await TicketAsync("north", Ann))["region"]);
    }

    [Fact]
    public async Task A_check_can_take_away_one_kind_of_action_only()
    {
        Install(a => a.HasPermission("Ticket", ctx => ctx.Action != PermAction.Delete));
        var ticket = await TicketAsync("north");

        Assert.Equal("north", (await _engine.Documents.GetAsync("Ticket", (string)ticket["name"]!, Ann))["region"]);
        await _engine.Documents.UpdateAsync("Ticket", (string)ticket["name"]!, Values(("title", "changed")), Ann);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Ticket", (string)ticket["name"]!, Ann));
    }

    [Fact]
    public async Task An_app_can_never_grant_what_roles_do_not_and_administrators_and_other_doctypes_are_not_affected()
    {
        Install(a => a.HasPermission("Ticket", _ => true).PermissionQuery("Ticket", _ => []).HasPermission("Ticket", ctx => ctx.User.User != TestUsers.Admin.User));
        var ticket = await TicketAsync("north");
        var viewer = new UserContext("vic@example.com", ["Viewer"]);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Ticket", (string)ticket["name"]!, viewer));
        Assert.Equal("north", (await _engine.Documents.GetAsync("Ticket", (string)ticket["name"]!, TestUsers.Admin))["region"]); // Administrators are exempt
        Assert.Equal(["north"], await RegionsSeenAsync(Ann)); // a query with no conditions narrows nothing
        Assert.NotEmpty(await RegionsSeenAsync(TestUsers.Admin));
    }

    [Fact]
    public async Task The_frameworks_own_doctypes_ignore_app_checks_so_an_app_cannot_lock_people_out()
    {
        Install(a => a.HasPermission("Role", _ => false).PermissionQuery("Role", _ => [new PermissionFilter("name", [])]));
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Auditor")), TestUsers.Admin);
        var manager = TestUsers.SystemManager;

        Assert.Equal("Auditor", (await _engine.Documents.GetAsync("Role", "Auditor", manager))["name"]);
        Assert.Single((await _engine.Documents.ListAsync("Role", new ListRequest { Limit = 10 }, manager)).Data);
    }

    [Fact]
    public async Task A_check_or_query_that_fails_refuses_and_lists_nothing_instead_of_letting_things_through()
    {
        Install(a => a
            .HasPermission("Ticket", _ => throw new InvalidOperationException("boom"))
            .PermissionQuery("Ticket", _ => throw new InvalidOperationException("boom")));
        var ticket = await TicketAsync("north");

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Ticket", (string)ticket["name"]!, Ann));
        Assert.Empty(await RegionsSeenAsync(Ann));
    }

    [Fact]
    public async Task A_query_naming_a_missing_or_secret_column_lists_nothing()
    {
        Install(a => a.PermissionQuery("Ticket", _ => [new PermissionFilter("ghost", ["x"])]));
        await TicketAsync("north");
        Assert.Empty(await RegionsSeenAsync(Ann));

        var other = new TestEngine();
        other.Registry.Add(_engine.Registry.Get("Ticket"));
        other.AppCatalog.Add(new RegionApp(a => a.PermissionQuery("Ticket", _ => [new PermissionFilter("secret", ["x"])])));
        await other.Documents.InsertAsync("Ticket", Values(("title", "t")), TestUsers.Admin);
        Assert.Empty((await other.Documents.ListAsync("Ticket", new ListRequest(), Ann)).Data);
    }

    [Fact]
    public void An_app_cannot_register_a_check_for_a_doctype_that_does_not_exist()
    {
        Install(a => a.HasPermission("Ghost", _ => true));

        Assert.Throws<InvalidOperationException>(() => _engine.AppCatalog.Validate(_engine.Registry));
    }
}
