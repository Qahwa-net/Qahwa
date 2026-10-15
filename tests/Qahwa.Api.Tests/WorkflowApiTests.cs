using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class WorkflowApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static object Workflow() => new
    {
        workflow = new
        {
            docType = "Leave Request",
            stateField = "workflow_state",
            active = true,
            states = new object[]
            {
                new { name = "Draft", color = "gray" },
                new { name = "Pending", color = "orange" },
                new { name = "Approved", color = "green" },
            },
            transitions = new object[]
            {
                new { from = "Draft", action = "Request", to = "Pending", roles = new[] { "Sales User" } },
                new { from = "Pending", action = "Approve", to = "Approved", roles = new[] { "Sales Manager" }, condition = "days <= 5" },
            },
        },
    };

    [Fact]
    public async Task Build_a_workflow_then_use_it_over_the_api()
    {
        var admin = _factory.ClientFor("Administrator");
        var created = await admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Leave Request",
                fields = new object[] { new { name = "days", type = "Int" }, new { name = "workflow_state", type = "Data" } },
                permissions = new object[]
                {
                    new { role = "Sales User", read = true, write = true, create = true },
                    new { role = "Sales Manager", read = true },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var saved = await sysmgr.PutAsJsonAsync("/api/workflows/doctypes/Leave%20Request", Workflow());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await JsonOf(saved)).GetProperty("data").GetProperty("exists").GetBoolean());

        var user = _factory.ClientFor("u@example.com", "Sales User");
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var id = (await JsonOf(await user.PostAsJsonAsync("/api/resource/Leave%20Request", new { days = 3, workflow_state = "Approved" }))).GetProperty("data").GetProperty("name").GetString();

        var status = (await JsonOf(await user.GetAsync($"/api/resource/Leave%20Request/{id}/workflow"))).GetProperty("data");
        Assert.Equal("Draft", status.GetProperty("state").GetString());
        Assert.Equal("Request", status.GetProperty("actions")[0].GetProperty("action").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/resource/Leave%20Request/{id}/workflow", new { action = "Approve" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsJsonAsync($"/api/resource/Leave%20Request/{id}/workflow", new { action = "Request" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PutAsJsonAsync($"/api/resource/Leave%20Request/{id}", new { workflow_state = "Approved" })).StatusCode);

        var pending = (await JsonOf(await manager.GetAsync("/api/workflow/pending"))).GetProperty("data");
        Assert.Equal(id, pending[0].GetProperty("name").GetString());
        var done = await manager.PostAsJsonAsync($"/api/resource/Leave%20Request/{id}/workflow", new { action = "Approve" });
        Assert.Equal("Approved", (await JsonOf(done)).GetProperty("data").GetProperty("state").GetString());
        Assert.Empty((await JsonOf(await manager.GetAsync("/api/workflow/pending"))).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task The_builder_checks_who_asks_and_what_is_sent()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/workflows/doctypes")).StatusCode);
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/workflows/doctypes")).StatusCode);
        // A guest has nothing waiting for them.
        var guest = await JsonOf(await _factory.CreateClient().GetAsync("/api/workflow/pending"));
        Assert.Empty(guest.GetProperty("data").EnumerateArray());

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.GetAsync("/api/workflows/doctypes")).StatusCode);
        var bad = await sysmgr.PutAsJsonAsync("/api/workflows/doctypes/Customer", new { workflow = new { docType = "Customer", stateField = "nope", states = Array.Empty<object>() } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/workflows/doctypes/Nope")).StatusCode);

        // A DocType without a workflow opens as an empty, inactive one the builder can fill.
        var empty = await sysmgr.GetAsync("/api/workflows/doctypes/Customer");
        Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
        var view = (await JsonOf(empty)).GetProperty("data");
        Assert.False(view.GetProperty("exists").GetBoolean());
        Assert.Empty(view.GetProperty("workflow").GetProperty("states").EnumerateArray());
    }
}
