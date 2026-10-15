using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class NotificationRuleApiTests : IAsyncDisposable
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
    public async Task Rules_built_over_the_api_tell_people_about_changes()
    {
        await PersonAsync("a@example.com", "Ann", "Sales User");
        await PersonAsync("b@example.com", "Bob", "Sales User");
        await _factory.ClientFor("Administrator").PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Memo Note",
                fields = new object[] { new { name = "subject", type = "Data" }, new { name = "status", type = "Data" } },
                permissions = new object[] { new { role = "Sales User", read = true, write = true, create = true } },
            },
        });

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var saved = await sysmgr.PutAsJsonAsync("/api/notification-rules/doctypes/Memo%20Note", new
        {
            rules = new
            {
                docType = "Memo Note",
                rules = new object[]
                {
                    new { name = "Closed", @event = "ValueChange", field = "status", condition = "status == 'Closed'", users = new[] { "b@example.com" }, message = "{{user}} closed {{subject}}" },
                },
            },
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.True((await JsonOf(saved)).GetProperty("data").GetProperty("exists").GetBoolean());

        var ann = _factory.ClientFor("a@example.com", "Sales User");
        var bob = _factory.ClientFor("b@example.com", "Sales User");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Memo%20Note", new { subject = "Budget", status = "Open" }))).GetProperty("data").GetProperty("name").GetString();
        Assert.Equal(0, (await JsonOf(await bob.GetAsync("/api/notifications?unread=true"))).GetProperty("data").GetArrayLength());

        await ann.PutAsJsonAsync($"/api/resource/Memo%20Note/{id}", new { status = "Closed" });

        var inbox = (await JsonOf(await bob.GetAsync("/api/notifications?unread=true"))).GetProperty("data");
        Assert.Equal(1, inbox.GetArrayLength());
        Assert.Equal("Alert", inbox[0].GetProperty("type").GetString());
        Assert.Equal("Ann closed Budget", inbox[0].GetProperty("subject").GetString());
    }

    [Fact]
    public async Task The_page_checks_who_asks_and_what_is_sent()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/notification-rules/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("m@example.com", "Sales Manager").GetAsync("/api/notification-rules/doctypes")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.GetAsync("/api/notification-rules/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/notification-rules/doctypes/ToDo")).StatusCode);

        var view = (await JsonOf(await sysmgr.GetAsync("/api/notification-rules/doctypes/Customer"))).GetProperty("data");
        Assert.False(view.GetProperty("exists").GetBoolean());
        Assert.Equal(0, view.GetProperty("set").GetProperty("rules").GetArrayLength());

        var bad = await sysmgr.PutAsJsonAsync("/api/notification-rules/doctypes/Customer", new
        {
            rules = new { docType = "Customer", rules = new object[] { new { name = "x", @event = "New", users = new[] { "ghost@example.com" }, message = "{{customer_name}}; DROP TABLE x; --{{" } } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }
}
