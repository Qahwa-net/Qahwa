using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class AssignmentRuleApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task PersonAsync(string email, string name, string role)
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Role", new { role_name = role });
        await admin.PostAsJsonAsync("/api/resource/User", new { email, full_name = name });
        await admin.PostAsJsonAsync("/api/resource/Has Role", new { user = email, role = role });
    }

    [Fact]
    public async Task Rules_built_over_the_api_assign_new_documents()
    {
        await PersonAsync("a@example.com", "Ann", "Sales User");
        await PersonAsync("b@example.com", "Bob", "Sales Manager");
        var created = await _factory.ClientFor("Administrator").PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Case File",
                fields = new object[] { new { name = "subject", type = "Data" }, new { name = "urgency", type = "Data" } },
                permissions = new object[] { new { role = "Sales User", read = true, write = true, create = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var saved = await sysmgr.PutAsJsonAsync("/api/assignment-rules/doctypes/Case%20File", new
        {
            rules = new
            {
                docType = "Case File",
                rules = new object[]
                {
                    new { name = "Urgent", strategy = "RoundRobin", users = new[] { "b@example.com" }, condition = "urgency == 'High'", priority = "High", dueInDays = 2, description = "Look now" },
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await JsonOf(saved)).GetProperty("data").GetProperty("exists").GetBoolean());

        var ann = _factory.ClientFor("a@example.com", "Sales User");
        var bob = _factory.ClientFor("b@example.com", "Sales Manager");
        await ann.PostAsJsonAsync("/api/resource/Case%20File", new { subject = "Calm", urgency = "Low" });
        await ann.PostAsJsonAsync("/api/resource/Case%20File", new { subject = "Fire", urgency = "High" });

        var mine = (await JsonOf(await bob.GetAsync("/api/assignments/mine"))).GetProperty("data");
        Assert.Equal(1, mine.GetArrayLength());
        Assert.Equal("Look now", mine[0].GetProperty("description").GetString());
        Assert.Equal("High", mine[0].GetProperty("priority").GetString());
    }

    [Fact]
    public async Task The_page_checks_who_asks_and_what_is_sent()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/assignment-rules/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("m@example.com", "Sales Manager").GetAsync("/api/assignment-rules/doctypes")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.GetAsync("/api/assignment-rules/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/assignment-rules/doctypes/ToDo")).StatusCode);

        var view = (await JsonOf(await sysmgr.GetAsync("/api/assignment-rules/doctypes/Customer"))).GetProperty("data");
        Assert.False(view.GetProperty("exists").GetBoolean());
        Assert.Equal(0, view.GetProperty("set").GetProperty("rules").GetArrayLength());

        var bad = await sysmgr.PutAsJsonAsync("/api/assignment-rules/doctypes/Customer", new
        {
            rules = new { docType = "Customer", rules = new object[] { new { name = "x", strategy = "RoundRobin", users = new[] { "ghost@example.com" }, condition = "'; DROP TABLE x; --" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
