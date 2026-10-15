using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class WorkspaceTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Boss"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]);

    public WorkspaceTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Order Form", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true }, { "role": "Boss", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Secret Form", "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Boss", "read": true } ] }
            """));
    }

    private async Task RolesAsync()
    {
        foreach (var role in new[] { "Clerk", "Boss", "Outsider" })
            await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
    }

    private static WorkspaceBlock Heading(string text) => new() { Type = WorkspaceBlock.Heading, Text = text };

    private static WorkspaceShortcut S(string label, string kind, string target) => new(label, kind, target);

    private static WorkspaceBlock Links(params WorkspaceShortcut[] items) => new() { Type = WorkspaceBlock.Shortcuts, Items = items };

    private Task<WorkspaceDetail> MakeAsync(string title, int sequence = 0, string[]? roles = null, params WorkspaceBlock[] blocks) =>
        _engine.Workspaces.CreateAsync(new WorkspaceRequest(title, sequence, roles, blocks), TestUsers.SystemManager);

    // ---- what people see ----

    [Fact]
    public async Task People_see_the_workspaces_for_their_roles_in_order()
    {
        await RolesAsync();
        await MakeAsync("Later", 20, null, Heading("L"));
        await MakeAsync("Sales", 10, ["Clerk"], Heading("S"));
        await MakeAsync("Management", 5, ["Boss"], Heading("M"));
        await MakeAsync("Everyone", 30, [], Heading("E"));

        Assert.Equal(["Sales", "Later", "Everyone"], (await _engine.Workspaces.MineAsync(Ann)).Select(w => w.Title));
        Assert.Equal(["Management", "Later", "Everyone"], (await _engine.Workspaces.MineAsync(Bob)).Select(w => w.Title));
        Assert.Equal(["Later", "Everyone"], (await _engine.Workspaces.MineAsync(Eve)).Select(w => w.Title));
        Assert.Equal(4, (await _engine.Workspaces.MineAsync(TestUsers.Admin)).Count);
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Workspaces.MineAsync(UserContext.Anonymous));
    }

    [Fact]
    public async Task Shortcuts_to_things_a_person_cannot_use_are_left_out()
    {
        await RolesAsync();
        await MakeAsync("Home", 0, null,
            Links(S("Orders", "DocType", "Order Form"), S("Secrets", "DocType", "Secret Form"), S("Dashboards", "Page", "dashboards"), S("Designer", "Page", "designer")));

        var ann = Assert.Single(await _engine.Workspaces.MineAsync(Ann)).Blocks.Single().Shortcuts!;
        Assert.Equal([("Orders", "/app/Order%20Form"), ("Dashboards", "/dashboards")], ann.Select(s => (s.Label, s.Path)));

        var bob = Assert.Single(await _engine.Workspaces.MineAsync(Bob)).Blocks.Single().Shortcuts!;
        Assert.Equal(["Orders", "Secrets", "Dashboards"], bob.Select(s => s.Label));

        var manager = Assert.Single(await _engine.Workspaces.MineAsync(TestUsers.SystemManager)).Blocks.Single().Shortcuts!;
        Assert.Contains(manager, s => s.Path == "/designer");
    }

    [Fact]
    public async Task A_block_with_nothing_the_person_may_use_disappears()
    {
        await RolesAsync();
        await MakeAsync("Home", 0, null, Heading("Hi"), Links(S("Secrets", "DocType", "Secret Form")));

        var blocks = Assert.Single(await _engine.Workspaces.MineAsync(Ann)).Blocks;

        Assert.Equal(["Heading"], blocks.Select(b => b.Type));
    }

    [Fact]
    public async Task A_dashboard_block_shows_only_dashboards_the_person_may_open()
    {
        await RolesAsync();
        var shared = await _engine.Dashboards.CreateAsync(new DashboardRequest("Team", true, []), Ann);
        var mine = await _engine.Dashboards.CreateAsync(new DashboardRequest("Mine", false, []), Ann);
        await MakeAsync("Home", 0, null,
            new WorkspaceBlock { Type = WorkspaceBlock.Dashboard, DashboardId = shared.Id },
            new WorkspaceBlock { Type = WorkspaceBlock.Dashboard, DashboardId = mine.Id },
            Links(S("Team board", "Dashboard", shared.Id), S("Mine", "Dashboard", mine.Id)));

        var bob = Assert.Single(await _engine.Workspaces.MineAsync(Bob)).Blocks;
        Assert.Equal(["Team"], bob.Where(b => b.Type == "Dashboard").Select(b => b.DashboardTitle));
        Assert.Equal([$"/dashboards/{shared.Id}"], bob.Single(b => b.Type == "Shortcuts").Shortcuts!.Select(s => s.Path));

        var ann = Assert.Single(await _engine.Workspaces.MineAsync(Ann)).Blocks;
        Assert.Equal(2, ann.Count(b => b.Type == "Dashboard"));
    }

    // ---- managing ----

    [Fact]
    public async Task Only_system_managers_manage_workspaces()
    {
        var made = await MakeAsync("Home", 0, null, Heading("x"));

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workspaces.ListAsync(Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workspaces.GetAsync(made.Info.Id, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workspaces.CreateAsync(new WorkspaceRequest("X"), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workspaces.UpdateAsync(made.Info.Id, new WorkspaceRequest("X"), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workspaces.DeleteAsync(made.Info.Id, Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Workspaces.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Workspaces.GetAsync("nope", TestUsers.SystemManager));
    }

    [Fact]
    public async Task A_workspace_can_be_changed_and_deleted()
    {
        await RolesAsync();
        var made = await MakeAsync("Home", 5, ["Clerk"], Heading("One"));

        var changed = await _engine.Workspaces.UpdateAsync(made.Info.Id, new WorkspaceRequest("Renamed", 9, ["Boss"], [Heading("Two"), Heading("Three")]), TestUsers.SystemManager);

        Assert.Equal(("Renamed", 9, 2), (changed.Info.Title, changed.Info.Sequence, changed.Blocks.Count));
        Assert.Equal(["Boss"], changed.Info.Roles);
        Assert.Single(await _engine.Workspaces.MineAsync(Bob));
        Assert.Empty(await _engine.Workspaces.MineAsync(Ann));

        await _engine.Workspaces.DeleteAsync(made.Info.Id, TestUsers.SystemManager);
        Assert.Empty(await _engine.Workspaces.ListAsync(TestUsers.SystemManager));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Workspaces.DeleteAsync(made.Info.Id, TestUsers.SystemManager));
    }

    [Fact]
    public async Task Problems_are_reported_by_block_and_field()
    {
        await RolesAsync();
        var request = new WorkspaceRequest("Home", 0, ["Ghost"],
        [
            new WorkspaceBlock { Type = "Banner" },
            Heading(""),
            new WorkspaceBlock { Type = WorkspaceBlock.Dashboard, DashboardId = "nope" },
            Links(),
            Links(S("", "DocType", "Order Form"), S("X", "DocType", "Nope"), S("Y", "Page", "../../etc"), S("Z", "Url", "https://evil.example"), S("W", "Page", "javascript:alert(1)")),
        ]);

        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Workspaces.CreateAsync(request, TestUsers.SystemManager))).Errors.Select(e => e.Field).ToHashSet();

        foreach (var expected in new[]
        {
            "roles", "blocks[0].type", "blocks[1].text", "blocks[2].dashboardId", "blocks[3].items",
            "blocks[4].items[0].label", "blocks[4].items[1].target", "blocks[4].items[2].target", "blocks[4].items[3].target", "blocks[4].items[4].target",
        })
            Assert.Contains(expected, errors);
    }

    [Fact]
    public async Task Titles_counts_and_positions_are_limited()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Workspaces.CreateAsync(new WorkspaceRequest(""), TestUsers.SystemManager));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Workspaces.CreateAsync(new WorkspaceRequest(new string('t', 101)), TestUsers.SystemManager));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Workspaces.CreateAsync(new WorkspaceRequest("T", -1), TestUsers.SystemManager));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Workspaces.CreateAsync(new WorkspaceRequest("T", 0, null, Enumerable.Range(0, 31).Select(i => Heading($"h{i}")).ToList()), TestUsers.SystemManager));

        for (var i = 0; i < WorkspaceService.MaxWorkspaces; i++)
            await MakeAsync($"W{i}", i, null, Heading("x"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => MakeAsync("One more"));
    }

    [Fact]
    public async Task Heading_text_is_plain_text_and_stays_so()
    {
        var made = await MakeAsync("Home", 0, null, Heading("<script>alert(1)</script> & co"));

        var shown = Assert.Single(await _engine.Workspaces.MineAsync(TestUsers.Admin)).Blocks.Single();

        Assert.Equal("<script>alert(1)</script> & co", shown.Text); // returned as data; the Desk renders it as text
        Assert.NotNull(made);
    }
}
