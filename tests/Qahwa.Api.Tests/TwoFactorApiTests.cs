using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class TwoFactorApiTests : IAsyncDisposable
{
    private const string Email = "ann@example.com";
    private const string Password = TestUsers.StrongPassword;

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static string Code(string secret, int ahead = 0) =>
        Totp.Code(Totp.FromBase32(secret)!, Totp.StepOf(DateTime.UtcNow) + ahead);

    private async Task<HttpClient> SignedInAsync()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", Password, ["System Manager"]);
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).StatusCode);
        return client;
    }

    /// <summary>Turns two-factor on for the user and returns the secret and a recovery code.</summary>
    private async Task<(string Secret, string Recovery)> TurnOnAsync(HttpClient client)
    {
        var setup = (await JsonOf(await client.PostAsync("/api/auth/2fa/setup", null))).GetProperty("data");
        var secret = setup.GetProperty("secret").GetString()!;
        Assert.StartsWith("otpauth://totp/Qahwa:", setup.GetProperty("uri").GetString());
        var enabled = await client.PostAsJsonAsync("/api/auth/2fa/enable", new { code = Code(secret) });
        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        var codes = (await JsonOf(enabled)).GetProperty("data").GetProperty("recovery_codes");
        Assert.Equal(10, codes.GetArrayLength());
        return (secret, codes[0].GetString()!);
    }

    [Fact]
    public async Task Once_on_the_password_alone_gets_no_session_and_a_code_finishes_the_sign_in()
    {
        var (secret, _) = await TurnOnAsync(await SignedInAsync());

        var fresh = _factory.CreateClient();
        var first = await fresh.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.False(first.Headers.Contains("Set-Cookie")); // no session yet
        var body = (await JsonOf(first)).GetProperty("data");
        Assert.True(body.GetProperty("two_factor").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/auth/me")).StatusCode);
        var token = body.GetProperty("token").GetString();

        var wrong = await fresh.PostAsJsonAsync("/api/auth/login/2fa", new { token, code = "000000" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_code", (await JsonOf(wrong)).GetProperty("code").GetString());

        var done = await fresh.PostAsJsonAsync("/api/auth/login/2fa", new { token, code = Code(secret, ahead: 1) });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Contains(done.Headers.GetValues("Set-Cookie"), c => c.StartsWith("qahwa.sid="));
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task A_forged_or_missing_challenge_never_signs_anyone_in()
    {
        var (secret, _) = await TurnOnAsync(await SignedInAsync());
        var client = _factory.CreateClient();

        foreach (var token in new[] { "", "garbage", "fake:" + Convert.ToBase64String("2fa|ann@example.com|9999999999999999999"u8.ToArray()) + ":0000000000000000" })
        {
            var response = await client.PostAsJsonAsync("/api/auth/login/2fa", new { token, code = Code(secret, 1) });
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.False(response.Headers.Contains("Set-Cookie"));
        }
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var (_, recovery) = await TurnOnAsync(await SignedInAsync());
        var token = (await JsonOf(await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }))).GetProperty("data").GetProperty("token").GetString();

        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login/2fa", new { token, code = recovery })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/login/2fa", new { token, code = recovery })).StatusCode);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_a_code()
    {
        var client = await SignedInAsync();
        var (secret, _) = await TurnOnAsync(client);

        var noPassword = await client.PostAsJsonAsync("/api/auth/2fa/disable", new { password = "wrong-password-xx", code = Code(secret, 1) });
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.StatusCode);
        var noCode = await client.PostAsJsonAsync("/api/auth/2fa/disable", new { password = Password, code = "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, noCode.StatusCode);
        Assert.True((await JsonOf(await client.GetAsync("/api/auth/2fa"))).GetProperty("data").GetProperty("enabled").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/2fa/disable", new { password = Password, code = Code(secret, 1) })).StatusCode);
        Assert.False((await JsonOf(await client.GetAsync("/api/auth/2fa"))).GetProperty("data").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Settings_need_a_signed_in_person_and_an_administrator_can_reset_someone_else()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/auth/2fa")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync("/api/auth/2fa/setup", null)).StatusCode);

        var client = await SignedInAsync();
        await TurnOnAsync(client);
        await _factory.Auth.EnsureUserAsync("clerk@example.com", "Clerk", Password, ["Clerk"]);
        var clerk = _factory.CreateClient();
        await clerk.PostAsJsonAsync("/api/auth/login", new { email = "clerk@example.com", password = Password });
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsJsonAsync("/api/auth/2fa/reset", new { user = Email })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/2fa/reset", new { user = Email })).StatusCode);
        var after = await _factory.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password });
        Assert.True(after.Headers.Contains("Set-Cookie")); // password alone is enough again
    }

    [Fact]
    public async Task An_api_key_cannot_see_or_change_two_factor_settings()
    {
        var client = await SignedInAsync();
        var key = (await JsonOf(await client.PostAsJsonAsync("/api/api-keys", new { description = "ci" }))).GetProperty("data").GetProperty("token").GetString();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);

        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.GetAsync("/api/auth/2fa")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bearer.PostAsync("/api/auth/2fa/setup", null)).StatusCode);
    }
}
