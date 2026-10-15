using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class EmailApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_document_can_be_emailed_from_a_template_and_the_message_is_queued()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Email Template", new { template_name = "Hello", subject = "About {{customer_name}}", body = "Dear {{customer_name}}, hello from {{user}}." });
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var templates = (await JsonOf(await ann.GetAsync("/api/email/templates?doctype=Customer"))).GetProperty("data");
        Assert.Equal("Hello", templates[0].GetProperty("name").GetString());

        var preview = (await JsonOf(await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/email/preview", new { template = "Hello" }))).GetProperty("data");
        Assert.Equal("About Acme", preview.GetProperty("subject").GetString());

        var sent = await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/email", new { to = new[] { "x@example.com" }, template = "Hello" });
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal(1, (await JsonOf(sent)).GetProperty("data").GetProperty("queued").GetInt32());
        var queue = (await JsonOf(await admin.GetAsync("/api/resource/Email Queue?fields=%5B%22recipient%22,%22subject%22%5D"))).GetProperty("data");
        Assert.Equal("x@example.com", queue[0].GetProperty("recipient").GetString());
    }

    [Fact]
    public async Task Callers_and_input_are_checked()
    {
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/email/templates?doctype=Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync($"/api/resource/Customer/{id}/email", new { to = new[] { "x@example.com" }, subject = "s", message = "m" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/email", new { to = new[] { "x@example.com\r\nBcc: e@example.com" }, subject = "s", message = "m" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/email", new { to = new[] { "x@example.com" }, subject = "", message = "m" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/email/preview", new { template = "Missing" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("o@example.com", "Outsider").PostAsJsonAsync($"/api/resource/Customer/{id}/email", new { to = new[] { "x@example.com" }, subject = "s", message = "m" })).StatusCode);
    }
}
