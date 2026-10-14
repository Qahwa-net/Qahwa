using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

/// <summary>Real cookie login against the real endpoints (only the storage is in memory).</summary>
public sealed class AuthApiTests : IAsyncDisposable
{
    private const string Email = "admin@example.com";
    private const string Password = TestUsers.StrongPassword;

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task SeedAsync(string email = Email, params string[] roles) =>
        await _factory.Auth.EnsureUserAsync(email, "Test User", Password, roles.Length > 0 ? roles : ["Administrator"]);

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email = Email, string password = Password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });

    [Fact]
    public async Task Login_sets_a_hardened_session_cookie_and_me_returns_the_user()
    {
        await SeedAsync();
        var client = _factory.CreateClient();

        var login = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("qahwa.sid="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookie, StringComparison.OrdinalIgnoreCase); // session cookie

        var me = (await JsonOf(await client.GetAsync("/api/auth/me"))).GetProperty("data");
        Assert.Equal(Email, me.GetProperty("user").GetString());
        Assert.Equal("Test User", me.GetProperty("full_name").GetString());
        Assert.Contains("Administrator", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Session_authorises_the_resource_api_until_logout()
    {
        await SeedAsync();
        var client = _factory.CreateClient();
        await LoginAsync(client);

        var created = await client.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(Email, (await JsonOf(created)).GetProperty("data").GetProperty("owner").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/resource/Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Roles_come_from_has_role_documents()
    {
        await SeedAsync("manager@example.com", "Sales Manager");
        var client = _factory.CreateClient();
        await LoginAsync(client, "manager@example.com");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/resource/Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/resource/User")).StatusCode);
    }

    [Fact]
    public async Task Bad_credentials_get_the_same_401_whether_or_not_the_user_exists()
    {
        await SeedAsync();

        var wrong = await LoginAsync(_factory.CreateClient(), password: "wrong password here");
        var unknown = await LoginAsync(_factory.CreateClient(), email: "ghost@example.com");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal("invalid_credentials", (await JsonOf(wrong)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Header_login_does_not_exist_in_the_real_pipeline()
    {
        await SeedAsync();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Qahwa-User", "Administrator");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/resource/Customer")).StatusCode);
    }

    [Fact]
    public async Task Malformed_login_bodies_never_succeed()
    {
        var client = _factory.CreateClient();

        var empty = await client.PostAsJsonAsync("/api/auth/login", new { });
        var notJson = await client.PostAsync("/api/auth/login", new StringContent("nope", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, empty.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);
    }

    [Fact]
    public async Task Disabling_a_user_ends_their_open_session()
    {
        await SeedAsync();
        var client = _factory.CreateClient();
        await LoginAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/resource/Customer")).StatusCode);

        var admin = _factory.CreateClient();
        await _factory.Auth.EnsureUserAsync("root@example.com", "Root", Password, ["Administrator"]);
        await LoginAsync(admin, "root@example.com");
        var disable = await admin.PutAsJsonAsync($"/api/resource/User/{Email}", new { enabled = false });
        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/resource/Customer")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_factory.CreateClient())).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_keeps_this_session_and_ends_others()
    {
        await SeedAsync();
        var first = _factory.CreateClient();
        var second = _factory.CreateClient();
        await LoginAsync(first);
        await LoginAsync(second);
        await Task.Delay(20); // password version is a timestamp

        var wrongOld = await first.PostAsJsonAsync("/api/auth/change-password",
            new { old_password = "not my password", new_password = "an entirely new password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongOld.StatusCode);

        var tooShort = await first.PostAsJsonAsync("/api/auth/change-password",
            new { old_password = Password, new_password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var changed = await first.PostAsJsonAsync("/api/auth/change-password",
            new { old_password = Password, new_password = "an entirely new password" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(_factory.CreateClient())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_factory.CreateClient(), password: "an entirely new password")).StatusCode);
    }

    [Fact]
    public async Task Change_password_requires_a_session()
    {
        var response = await _factory.CreateClient().PostAsJsonAsync("/api/auth/change-password",
            new { old_password = Password, new_password = "an entirely new password" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        await SeedAsync();
        for (var i = 0; i < 5; i++)
            await LoginAsync(_factory.CreateClient(), password: "wrong password " + i);

        var response = await LoginAsync(_factory.CreateClient());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client()
    {
        await using var limited = new QahwaFactory(realAuth: true, loginPermitsPerMinute: 3);
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await LoginAsync(client, password: "wrong password " + i)).StatusCode);

        Assert.Equal(
            [HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized,
             HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests],
            statuses);
    }
}
