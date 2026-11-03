using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

/// <summary>Imports of big files in the background, against the real pipeline (the import runs as the real, signed-in person).</summary>
public sealed class BackgroundImportApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task<HttpClient> SignedInAsync(string email, params string[] roles)
    {
        await _factory.Auth.EnsureUserAsync(email, "Person", TestUsers.StrongPassword, roles);
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestUsers.StrongPassword })).StatusCode);
        return client;
    }

    [Fact]
    public async Task A_file_is_queued_runs_as_the_person_and_its_progress_can_be_followed()
    {
        var ann = await SignedInAsync("ann@example.com", "Sales Manager");
        var csv = "customer_name\r\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"Customer {i}\r\n"));

        var queued = await ann.PostAsJsonAsync("/api/import/Customer/background", new { csv, mode = "Insert" });
        Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode);
        var id = (await JsonOf(queued)).GetProperty("data").GetProperty("id").GetString();

        JsonElement run = default;
        for (var i = 0; i < 100; i++)
        {
            run = (await JsonOf(await ann.GetAsync($"/api/import-runs/{id}"))).GetProperty("data");
            if (run.GetProperty("status").GetString() is "Done" or "Failed")
                break;
            await Task.Delay(100);
        }

        Assert.Equal("Done", run.GetProperty("status").GetString());
        Assert.Equal((40, 40), (run.GetProperty("created").GetInt32(), run.GetProperty("rows").GetInt32()));
        var customers = (await JsonOf(await ann.GetAsync("/api/resource/Customer?fields=%5B%22owner%22%5D&limit_page_length=100"))).GetProperty("data");
        Assert.Equal(40, customers.GetArrayLength());
        Assert.All(customers.EnumerateArray(), c => Assert.Equal("ann@example.com", c.GetProperty("owner").GetString()));

        var mine = (await JsonOf(await ann.GetAsync("/api/import-runs"))).GetProperty("data");
        Assert.Equal(1, mine.GetArrayLength());
    }

    [Fact]
    public async Task Callers_files_and_other_peoples_runs_are_checked()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/import/Customer/background", new { csv = "customer_name\nX\n" })).StatusCode);

        var outsider = await SignedInAsync("o@example.com", "Outsider");
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync("/api/import/Customer/background", new { csv = "customer_name\nX\n" })).StatusCode);

        var ann = await SignedInAsync("ann@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/api/import/Customer/background", new { csv = "nothing,matches\n1,2\n" })).StatusCode);
        var started = await ann.PostAsJsonAsync("/api/import/Customer/background", new { csv = "customer_name\r\nOne\r\n" });
        var id = (await JsonOf(started)).GetProperty("data").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/import-runs/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.PostAsync($"/api/import-runs/{id}/cancel", null)).StatusCode);
    }
}
