using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class AutoRepeatApiTests : IAsyncDisposable
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

    private static string Tomorrow => DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");

    [Fact]
    public async Task A_document_can_be_set_to_repeat_and_made_to_copy_itself_now()
    {
        await PersonAsync("a@example.com", "Ann", "Sales User");
        await _factory.ClientFor("Administrator").PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Standing Order",
                fields = new object[] { new { name = "customer", type = "Data" }, new { name = "needed_on", type = "Date" } },
                permissions = new object[] { new { role = "Sales User", read = true, write = true, create = true } },
            },
        });
        var ann = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Standing%20Order", new { customer = "Acme", needed_on = "2026-01-01" }))).GetProperty("data").GetProperty("name").GetString();

        var created = await ann.PostAsJsonAsync($"/api/resource/Standing%20Order/{id}/auto-repeat", new { frequency = "Weekly", startDate = Tomorrow, dateFields = new[] { "needed_on" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var repeat = (await JsonOf(created)).GetProperty("data");
        Assert.Equal("Weekly", repeat.GetProperty("frequency").GetString());
        Assert.Equal("Active", repeat.GetProperty("status").GetString());

        var listed = (await JsonOf(await ann.GetAsync($"/api/resource/Standing%20Order/{id}/auto-repeat"))).GetProperty("data");
        Assert.Equal(1, listed.GetArrayLength());
        Assert.Equal(1, (await JsonOf(await ann.GetAsync("/api/auto-repeat"))).GetProperty("data").GetArrayLength());

        var now = await ann.PostAsync($"/api/auto-repeat/{repeat.GetProperty("id").GetString()}/run", null);
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Equal(1, (await JsonOf(now)).GetProperty("data").GetProperty("created").GetInt32());
        Assert.Equal(2, (await JsonOf(await ann.GetAsync("/api/resource/Standing%20Order?fields=%5B%22name%22%5D"))).GetProperty("data").GetArrayLength());

        var paused = await ann.PutAsJsonAsync($"/api/auto-repeat/{repeat.GetProperty("id").GetString()}", new { status = "Paused" });
        Assert.Equal("Paused", (await JsonOf(paused)).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await ann.DeleteAsync($"/api/auto-repeat/{repeat.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(0, (await JsonOf(await ann.GetAsync("/api/auto-repeat"))).GetProperty("data").GetArrayLength());
    }

    [Fact]
    public async Task Callers_and_input_are_checked()
    {
        await PersonAsync("a@example.com", "Ann", "Sales Manager");
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/auto-repeat")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync($"/api/resource/Customer/{id}/auto-repeat", new { frequency = "Daily", startDate = Tomorrow })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/auto-repeat", new { frequency = "'; DROP TABLE x; --", startDate = Tomorrow })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/auto-repeat", new { frequency = "Daily", startDate = "2001-01-01" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.PutAsJsonAsync("/api/auto-repeat/nope", new { status = "Paused" })).StatusCode);

        var other = _factory.ClientFor("b@example.com", "Sales Manager");
        var created = await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/auto-repeat", new { frequency = "Daily", startDate = Tomorrow });
        var repeatId = (await JsonOf(created)).GetProperty("data").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/auto-repeat/{repeatId}")).StatusCode); // not theirs
    }
}
