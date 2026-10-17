using System.Net;
using System.Net.Http.Json;
using Qahwa.Core;

namespace Qahwa.Api.Tests;

public sealed class DeskSettingsApiTests : IAsyncDisposable
{
    private readonly QahwaFactory factory = new();
    public ValueTask DisposeAsync() => factory.DisposeAsync();
    private HttpClient Admin => factory.ClientFor("Administrator");
    private async Task<DeskState> State(HttpClient client) => (await client.GetFromJsonAsync<Envelope<DeskState>>("/api/desk-settings"))!.Data;
    private sealed record Envelope<T>(T Data);
    private async Task CreateAsync() => Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/designer/doctypes", new
    {
        definition = new { name = "Desk Note", module = "Notes", fields = new[] { new { name = "title", type = "Data" } }, permissions = new[] { new { role = "Employee", read = true } } },
    })).StatusCode);

    [Fact]
    public async Task Settings_are_personal_permission_filtered_and_persisted()
    {
        await CreateAsync();
        var ann = factory.ClientFor("ann@example.com", "Employee");
        var vic = factory.ClientFor("vic@example.com", "Employee");
        var state = await State(ann);
        Assert.Contains(state.Links, l => l.Id == "doctype:Desk Note");
        Assert.DoesNotContain(state.Links, l => l.Id == "page:designer");
        var layout = new DeskLayout { Pinned = ["doctype:Desk Note"], Hidden = ["page:reports"], CollapsedGroups = ["Notes"], HomeShortcuts = ["doctype:Desk Note"] };
        Assert.Equal(HttpStatusCode.OK, (await ann.PutAsJsonAsync("/api/desk-settings", new DeskSaveRequest(layout))).StatusCode);
        var saved = await State(factory.ClientFor("ann@example.com", "Employee"));
        Assert.True(saved.Personal);
        Assert.Equal(layout.Pinned, saved.Layout.Pinned);
        Assert.Equal(layout.Hidden, saved.Layout.Hidden);
        Assert.False((await State(vic)).Personal);
        Assert.Equal(HttpStatusCode.Conflict, (await ann.PutAsJsonAsync("/api/desk-settings", new DeskSaveRequest(layout))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.DeleteAsync("/api/desk-settings")).StatusCode);
        Assert.Empty((await State(ann)).Layout.Pinned);
    }

    [Fact]
    public async Task Role_defaults_apply_until_personalized_and_reset_restores_inheritance()
    {
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/resource/Role", new { role_name = "Employee" })).StatusCode);
        var ann = factory.ClientFor("ann@example.com", "Employee");
        var defaults = new DeskLayout { Pinned = ["page:reports"] };
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.PutAsJsonAsync("/api/desk-settings/roles/Employee", new DeskSaveRequest(defaults))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.PutAsJsonAsync("/api/desk-settings/roles/Employee", new DeskSaveRequest(defaults))).StatusCode);
        var inherited = await State(ann);
        Assert.False(inherited.Personal); Assert.Equal("Employee", inherited.DefaultRole); Assert.Equal(defaults.Pinned, inherited.Layout.Pinned);
        await ann.PutAsJsonAsync("/api/desk-settings", new DeskSaveRequest(new DeskLayout { Pinned = ["page:dashboards"] }));
        Assert.Equal("page:dashboards", Assert.Single((await State(ann)).Layout.Pinned));
        await ann.DeleteAsync("/api/desk-settings");
        Assert.Equal("page:reports", Assert.Single((await State(ann)).Layout.Pinned));
        await Admin.DeleteAsync("/api/desk-settings/roles/Employee");
        Assert.Null((await State(ann)).DefaultRole);
    }

    [Fact]
    public async Task Forged_links_and_inaccessible_workspaces_are_rejected_and_lost_permissions_are_pruned()
    {
        await CreateAsync();
        var ann = factory.ClientFor("ann@example.com", "Employee");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/desk-settings")).StatusCode);
        foreach (var id in new[] { "page:designer", "https://evil.example", "javascript:alert(1)" })
            Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/api/desk-settings", new DeskSaveRequest(new DeskLayout { Pinned = [id] }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PutAsJsonAsync("/api/desk-settings", new DeskSaveRequest(new DeskLayout { DefaultWorkspace = "private" }))).StatusCode);
        await ann.PutAsJsonAsync("/api/desk-settings", new DeskSaveRequest(new DeskLayout { Pinned = ["doctype:Desk Note"], HomeShortcuts = ["doctype:Desk Note"] }));
        var noRole = await State(factory.ClientFor("ann@example.com"));
        Assert.DoesNotContain(noRole.Links, l => l.Id == "doctype:Desk Note");
        Assert.Empty(noRole.Layout.Pinned); Assert.Empty(noRole.Layout.HomeShortcuts);
    }
}
