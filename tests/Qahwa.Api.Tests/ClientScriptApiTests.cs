using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ClientScriptApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_script_is_served_to_people_who_can_open_the_doctype_and_nobody_else()
    {
        var admin = _factory.ClientFor("Administrator");
        var made = await admin.PostAsJsonAsync("/api/resource/Client Script", new { title = "Greeting", reference_doctype = "Role", script = "qahwa.ui.form.on('Role', { refresh(frm) { frm.set_intro('Hello'); } });" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var seen = (await admin.GetFromJsonAsync<JsonElement>("/api/client-scripts/Role")).GetProperty("data");
        Assert.Equal("Greeting", seen[0].GetProperty("title").GetString());
        Assert.Contains("set_intro", seen[0].GetProperty("source").GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/client-scripts/Role")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("c@example.com", "Nobody").GetAsync("/api/client-scripts/Role")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/client-scripts/Nothing")).StatusCode);
    }

    [Fact]
    public async Task Only_a_manager_can_save_scripts_and_a_broken_one_is_refused()
    {
        var clerk = _factory.ClientFor("c@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync("/api/resource/Client Script", new { title = "x", reference_doctype = "Role", script = "1;" })).StatusCode);

        var bad = await _factory.ClientFor("Administrator").PostAsJsonAsync("/api/resource/Client Script", new { title = "x", reference_doctype = "Role", script = "function (" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task The_sandbox_worker_gets_a_policy_of_its_own_and_every_other_page_keeps_the_strict_one()
    {
        var worker = await _factory.CreateClient().GetAsync("/client-script-worker.js");
        var policy = string.Join(" ", worker.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'none'", policy);
        Assert.Contains("connect-src 'none'", policy); // no network of any kind from inside
        Assert.Contains("'unsafe-eval'", policy);

        foreach (var path in new[] { "/", "/api/health", "/client-script-worker.js.evil", "/CLIENT-SCRIPT-WORKER.JS" })
        {
            var other = string.Join(" ", (await _factory.CreateClient().GetAsync(path)).Headers.GetValues("Content-Security-Policy"));
            Assert.DoesNotContain("unsafe-eval", other);
            Assert.Contains("default-src 'self'", other);
        }
    }
}
