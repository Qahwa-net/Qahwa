using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ReminderApiTests : IAsyncDisposable
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

    private static string In(TimeSpan span) => DateTimeOffset.UtcNow.Add(span).ToString("O");

    [Fact]
    public async Task A_reminder_is_set_listed_and_cancelled()
    {
        await PersonAsync("a@example.com", "Ann", "Sales Manager");
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var created = await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/reminders", new { remindAt = In(TimeSpan.FromDays(2)), message = "Renew" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reminder = (await JsonOf(created)).GetProperty("data");
        Assert.Equal("Pending", reminder.GetProperty("status").GetString());
        Assert.Equal("Renew", reminder.GetProperty("message").GetString());

        Assert.Equal(1, (await JsonOf(await ann.GetAsync($"/api/resource/Customer/{id}/reminders"))).GetProperty("data").GetArrayLength());
        Assert.Equal(1, (await JsonOf(await ann.GetAsync("/api/reminders"))).GetProperty("data").GetArrayLength());

        Assert.Equal(HttpStatusCode.OK, (await ann.DeleteAsync($"/api/reminders/{reminder.GetProperty("id").GetString()}")).StatusCode);
        Assert.Equal(0, (await JsonOf(await ann.GetAsync("/api/reminders"))).GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.Conflict, (await ann.DeleteAsync($"/api/reminders/{reminder.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Callers_and_input_are_checked()
    {
        await PersonAsync("a@example.com", "Ann", "Sales Manager");
        await PersonAsync("b@example.com", "Bob", "Sales User");
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = (await JsonOf(await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/reminders")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/reminders", new { remindAt = "soon" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/reminders", new { remindAt = In(TimeSpan.FromHours(-1)) })).StatusCode);

        var mine = (await JsonOf(await ann.PostAsJsonAsync($"/api/resource/Customer/{id}/reminders", new { remindAt = In(TimeSpan.FromDays(1)) }))).GetProperty("data").GetProperty("id").GetString();
        var bob = _factory.ClientFor("b@example.com", "Sales User");
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/reminders/{mine}")).StatusCode);
        Assert.Equal(0, (await JsonOf(await bob.GetAsync("/api/reminders"))).GetProperty("data").GetArrayLength());
    }
}
