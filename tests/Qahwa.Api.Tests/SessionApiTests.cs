using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class SessionApiTests : IAsyncDisposable
{
    private const string Email = "ann@example.com";
    private const string Password = TestUsers.StrongPassword;

    private readonly QahwaFactory _factory = new(realAuth: true);
    private readonly HashSet<string> _seeded = [];

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task<HttpClient> DeviceAsync(string agent, string email = Email)
    {
        if (_seeded.Add(email))
            await _factory.Auth.EnsureUserAsync(email, "Ann", Password, ["System Manager"]); // setting a password again would end the earlier sessions
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd(agent);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = Password })).StatusCode);
        return client;
    }

    [Fact]
    public async Task Each_sign_in_is_listed_and_one_device_can_end_another()
    {
        var laptop = await DeviceAsync("Laptop/1.0");
        var phone = await DeviceAsync("Phone/2.0");

        var list = (await JsonOf(await laptop.GetAsync("/api/auth/sessions"))).GetProperty("data");
        Assert.Equal(2, list.GetArrayLength());
        var phoneEntry = list.EnumerateArray().Single(s => s.GetProperty("agent").GetString() == "Phone/2.0");
        var laptopEntry = list.EnumerateArray().Single(s => s.GetProperty("agent").GetString() == "Laptop/1.0");
        Assert.True(laptopEntry.GetProperty("current").GetBoolean());
        Assert.False(phoneEntry.GetProperty("current").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await laptop.DeleteAsync($"/api/auth/sessions/{phoneEntry.GetProperty("id").GetString()}")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode); // ended at once
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await laptop.DeleteAsync($"/api/auth/sessions/{phoneEntry.GetProperty("id").GetString()}")).StatusCode);
    }

    [Fact]
    public async Task Sign_out_everywhere_else_keeps_this_device()
    {
        var laptop = await DeviceAsync("Laptop/1.0");
        var phone = await DeviceAsync("Phone/2.0");
        var tablet = await DeviceAsync("Tablet/3.0");

        var done = await laptop.PostAsync("/api/auth/sessions/revoke-others", null);

        Assert.Equal(2, (await JsonOf(done)).GetProperty("data").GetProperty("revoked").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tablet.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Nobody_can_see_or_end_another_persons_sessions()
    {
        var ann = await DeviceAsync("Ann/1");
        var bob = await DeviceAsync("Bob/1", "bob@example.com");
        var annId = (await JsonOf(await ann.GetAsync("/api/auth/sessions"))).GetProperty("data")[0].GetProperty("id").GetString();

        var bobsView = await JsonOf(await bob.GetAsync("/api/auth/sessions"));
        Assert.Single(bobsView.GetProperty("data").EnumerateArray());
        Assert.DoesNotContain(annId!, bobsView.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/auth/sessions/{annId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_system_manager_can_sign_someone_out_everywhere_and_others_cannot()
    {
        var admin = await DeviceAsync("Admin/1");
        await _factory.Auth.EnsureUserAsync("clerk@example.com", "Clerk", Password, ["Clerk"]);
        var clerk = _factory.CreateClient();
        await clerk.PostAsJsonAsync("/api/auth/login", new { email = "clerk@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync("/api/auth/sessions/revoke-user", new { user = Email })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/auth/sessions/revoke-user", new { user = "clerk@example.com" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await clerk.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_ends_the_other_sessions_and_keeps_this_one()
    {
        var laptop = await DeviceAsync("Laptop/1.0");
        var phone = await DeviceAsync("Phone/2.0");

        var changed = await laptop.PostAsJsonAsync("/api/auth/change-password", new { old_password = Password, new_password = "a-brand-new-password-9" });

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/auth/me")).StatusCode);
        Assert.Single((await JsonOf(await laptop.GetAsync("/api/auth/sessions"))).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task A_cookie_without_a_live_session_is_nobody_and_api_keys_cannot_list_sessions()
    {
        var device = await DeviceAsync("Laptop/1.0");
        var key = (await JsonOf(await device.PostAsJsonAsync("/api/api-keys", new { description = "ci" }))).GetProperty("data").GetProperty("token").GetString();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/auth/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/auth/sessions")).StatusCode);
    }
}
