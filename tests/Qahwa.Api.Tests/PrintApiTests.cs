using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class PrintApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_document_prints_with_a_custom_format_and_a_strict_security_policy()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Receipt",
                fields = new object[] { new { name = "payer", type = "Data", label = "Payer" } },
                permissions = new object[] { new { role = "Sales User", read = true, write = true, create = true } },
            },
        });
        var ann = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Receipt", new { payer = "<img src=x onerror=alert(1)>" }))).GetProperty("data").GetProperty("name").GetString();

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var saved = await sysmgr.PutAsJsonAsync("/api/print-formats", new { name = "Plain", docType = "Receipt", html = "<h1>Receipt for {{payer}}</h1>", isDefault = true });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var choices = (await JsonOf(await ann.GetAsync("/api/print/choices?doctype=Receipt"))).GetProperty("data");
        Assert.Equal("Plain", choices.GetProperty("formats")[0].GetProperty("name").GetString());

        var page = await ann.GetAsync($"/api/print/Receipt/{id}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.StartsWith("text/html", page.Content.Headers.ContentType!.MediaType!);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Receipt for &lt;img src=x onerror=alert(1)&gt;</h1>", html);
        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'none'", csp);
        Assert.Matches("script-src 'nonce-[A-Za-z0-9+/=]+'", csp);
        Assert.Contains("nosniff", page.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("no-store", page.Headers.GetValues("Cache-Control").Single());
    }

    [Fact]
    public async Task Callers_and_input_are_checked()
    {
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/print/Customer/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("o@example.com", "Outsider").GetAsync($"/api/print/Customer/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/print/Customer/{id}?format=Missing")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.GetAsync("/api/print-formats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ann.PutAsJsonAsync("/api/letter-heads", new { name = "X" })).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.BadRequest, (await sysmgr.PutAsJsonAsync("/api/print-formats", new { name = "Bad", docType = "Customer", html = "<script>alert(1)</script>" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sysmgr.PutAsJsonAsync("/api/print-formats", new { name = "../x", docType = "Customer", html = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await sysmgr.PutAsJsonAsync("/api/letter-heads", new { name = "Head", header = "<img src=x onerror=y>" })).StatusCode);

        var head = await sysmgr.PutAsJsonAsync("/api/letter-heads", new { name = "Head", header = "<b>ACME</b>", footer = "Bye", isDefault = true });
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        var html = await (await ann.GetAsync($"/api/print/Customer/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("<b>ACME</b>", html);
    }
}
