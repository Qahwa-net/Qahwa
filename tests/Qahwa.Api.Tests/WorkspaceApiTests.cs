using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class WorkspaceApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_workspace_is_built_by_a_manager_and_shown_to_the_people_it_is_for()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var created = await sysmgr.PostAsJsonAsync("/api/workspaces", new
        {
            title = "Selling",
            sequence = 1,
            roles = Array.Empty<string>(),
            blocks = new object[]
            {
                new { type = "Heading", text = "Quick links" },
                new { type = "Shortcuts", items = new object[] { new { label = "Customers", kind = "DocType", target = "Customer" }, new { label = "Settings", kind = "Page", target = "designer" } } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var mine = (await JsonOf(await ann.GetAsync("/api/workspaces/mine"))).GetProperty("data");
        Assert.Equal("Selling", mine[0].GetProperty("title").GetString());
        var shortcuts = mine[0].GetProperty("blocks")[1].GetProperty("shortcuts");
        Assert.Equal(1, shortcuts.GetArrayLength()); // the designer is for System Managers only
        Assert.Equal("/app/Customer", shortcuts[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task Callers_and_input_are_checked()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/workspaces/mine")).StatusCode);
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.GetAsync("/api/workspaces")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.PostAsJsonAsync("/api/workspaces", new { title = "X" })).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var bad = await sysmgr.PostAsJsonAsync("/api/workspaces", new
        {
            title = "Bad",
            blocks = new object[] { new { type = "Shortcuts", items = new object[] { new { label = "Go", kind = "Page", target = "javascript:alert(1)" } } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/workspaces/nope")).StatusCode);
    }
}
