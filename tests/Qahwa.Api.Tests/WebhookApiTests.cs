using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class WebhookApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static object Body(string url = "https://hooks.example.com/x", string? secret = "a-long-enough-shared-secret", string name = "Notify") => new
    {
        hooks = new { docType = "Customer", hooks = new object[] { new { name, @event = "New", url, secret } } },
    };

    [Fact]
    public async Task A_webhook_is_saved_by_a_manager_and_its_secret_is_never_returned()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");

        var saved = await sysmgr.PutAsJsonAsync("/api/webhooks/doctypes/Customer", Body());
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var text = await saved.Content.ReadAsStringAsync();
        Assert.DoesNotContain("a-long-enough-shared-secret", text);
        var hook = JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("set").GetProperty("hooks")[0];
        Assert.True(hook.GetProperty("secretSet").GetBoolean());

        var again = await sysmgr.GetStringAsync("/api/webhooks/doctypes/Customer");
        Assert.DoesNotContain("a-long-enough-shared-secret", again);

        var modified = JsonDocument.Parse(text).RootElement.GetProperty("data").GetProperty("modified").GetString();
        var keep = await sysmgr.PutAsJsonAsync("/api/webhooks/doctypes/Customer", new
        {
            hooks = new { docType = "Customer", hooks = new object[] { new { name = "Notify", @event = "Update", url = "https://hooks.example.com/x", secret = "" } } },
            modified,
        });
        Assert.Equal(HttpStatusCode.OK, keep.StatusCode); // a blank secret keeps the stored one
    }

    [Fact]
    public async Task Callers_and_addresses_are_checked()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/webhooks/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("m@example.com", "Sales Manager").GetAsync("/api/webhooks/doctypes")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        foreach (var bad in new[] { "http://hooks.example.com/x", "https://localhost/x", "https://169.254.169.254/latest", "https://10.0.0.5/x", "https://u:p@hooks.example.com/x" })
            Assert.Equal(HttpStatusCode.BadRequest, (await sysmgr.PutAsJsonAsync("/api/webhooks/doctypes/Customer", Body(bad))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sysmgr.PutAsJsonAsync("/api/webhooks/doctypes/Customer", Body(secret: "short"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sysmgr.PutAsJsonAsync("/api/webhooks/doctypes/Customer", Body(secret: null))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/webhooks/doctypes/ToDo")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.DeleteAsync("/api/webhooks/doctypes/Customer")).StatusCode);
    }
}
