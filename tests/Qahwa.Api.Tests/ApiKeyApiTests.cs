using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

/// <summary>API keys against the real pipeline (real cookie login to make the key, then the key on its own).</summary>
public sealed class ApiKeyApiTests : IAsyncDisposable
{
    private const string Email = "dev@example.com";

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task<HttpClient> SignedInAsync(string email = Email, params string[] roles)
    {
        await _factory.Auth.EnsureUserAsync(email, "Dev", TestUsers.StrongPassword, roles.Length > 0 ? roles : ["Sales Manager"]);
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestUsers.StrongPassword })).StatusCode);
        return client;
    }

    private HttpClient WithKey(string token)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<(string Id, string Token)> MakeKeyAsync(HttpClient signedIn, object body)
    {
        var created = await signedIn.PostAsJsonAsync("/api/api-keys", body);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var data = (await JsonOf(created)).GetProperty("data");
        return (data.GetProperty("info").GetProperty("id").GetString()!, data.GetProperty("token").GetString()!);
    }

    [Fact]
    public async Task A_key_works_without_a_cookie_as_its_person_and_stops_when_revoked()
    {
        var me = await SignedInAsync();
        var (id, token) = await MakeKeyAsync(me, new { description = "script" });
        var script = WithKey(token);

        var user = await script.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, user.StatusCode);
        Assert.Equal(Email, (await JsonOf(user)).GetProperty("data").GetProperty("user").GetString());
        Assert.Equal(HttpStatusCode.Created, (await script.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Via key" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await me.DeleteAsync($"/api/api-keys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_read_only_key_can_look_but_not_change()
    {
        var me = await SignedInAsync();
        var (_, token) = await MakeKeyAsync(me, new { description = "viewer", readOnly = true });
        var viewer = WithKey(token);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/resource/Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync("/api/resource/Customer/x")).StatusCode);
    }

    [Fact]
    public async Task A_key_has_the_permissions_of_its_person_and_cannot_make_more_keys()
    {
        var me = await SignedInAsync("nobody@example.com", "Outsider");
        var (_, token) = await MakeKeyAsync(me, new { description = "weak" });
        var script = WithKey(token);

        Assert.Equal(HttpStatusCode.Forbidden, (await script.GetAsync("/api/resource/Customer")).StatusCode);
        var again = await script.PostAsJsonAsync("/api/api-keys", new { description = "a key making a key" });
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.DeleteAsync("/api/api-keys/anything")).StatusCode);
    }

    [Fact]
    public async Task Bad_tokens_are_nobody_and_a_list_never_shows_a_secret()
    {
        var me = await SignedInAsync();
        var (_, token) = await MakeKeyAsync(me, new { description = "list me" });

        foreach (var bad in new[] { "qk_000000000000.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "qk_", "qk_x.y", token + "x", "notakey" })
            Assert.Equal(HttpStatusCode.Unauthorized, (await WithKey(bad).GetAsync("/api/auth/me")).StatusCode);

        var list = await me.GetStringAsync("/api/api-keys");
        Assert.DoesNotContain(token[(token.IndexOf('.') + 1)..], list);
        Assert.Contains("list me", list);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/api-keys")).StatusCode);
    }

    [Fact]
    public async Task Making_a_key_is_checked()
    {
        var me = await SignedInAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/api-keys", new { description = new string('x', 200) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await me.PostAsJsonAsync("/api/api-keys", new { description = "d", expiresOn = "yesterday" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await me.DeleteAsync("/api/api-keys/nope")).StatusCode);
    }
}
