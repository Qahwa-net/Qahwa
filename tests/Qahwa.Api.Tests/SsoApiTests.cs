using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class SsoApiTests : IAsyncDisposable
{
    private const string Email = "ann@example.com";

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static string Query(Uri url, string name) =>
        Uri.UnescapeDataString(url.Query.TrimStart('?').Split('&').Single(p => p.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);

    private async Task<(HttpClient Client, Uri ToProvider)> StartAsync(string next = "/app/Customer")
    {
        var client = Browser();
        var response = await client.GetAsync("/api/auth/sso/idp/start?next=" + Uri.EscapeDataString(next));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (client, response.Headers.Location!);
    }

    [Fact]
    public async Task The_login_page_can_ask_which_providers_exist()
    {
        var list = (await Browser().GetFromJsonAsync<JsonElement>("/api/auth/sso")).GetProperty("data");

        Assert.Equal("idp", list[0].GetProperty("name").GetString());
        Assert.Equal("Company login", list[0].GetProperty("display_name").GetString());
        Assert.DoesNotContain("s3cret", list.GetRawText()); // the secret never leaves the server
        Assert.DoesNotContain("Authority", list.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starting_sends_the_browser_to_the_provider_and_keeps_the_proof_in_a_private_cookie()
    {
        var response = await Browser().GetAsync("/api/auth/sso/idp/start");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var to = response.Headers.Location!;
        Assert.Equal("idp.example.com", to.Host);
        Assert.Equal("S256", Query(to, "code_challenge_method"));
        Assert.Equal("http://localhost/api/auth/sso/idp/callback", Query(to, "redirect_uri"));
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("qahwa.sso=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Query(to, "state"), cookie); // encrypted, not the state itself
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task An_unknown_provider_goes_back_to_the_login_page_with_a_short_code()
    {
        var response = await Browser().GetAsync("/api/auth/sso/nope/start");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?sso_error=unknown_provider", response.Headers.Location!.OriginalString);
        var odd = await Browser().GetAsync("/api/auth/sso/%2e%2e/start");
        Assert.StartsWith("/login?sso_error=", odd.Headers.Location?.OriginalString ?? "/login?sso_error=");
    }

    [Fact]
    public async Task The_whole_round_trip_signs_the_person_in_and_lands_on_the_page_they_wanted()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, to) = await StartAsync("/app/Customer");
        _factory.IdentityProvider.NextToken = _factory.IdentityProvider.Issue("sub-ann", Email, true, nonce: Query(to, "nonce"));

        var back = await client.GetAsync($"/api/auth/sso/idp/callback?code=abc&state={Uri.EscapeDataString(Query(to, "state"))}");

        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/app/Customer", back.Headers.Location!.OriginalString);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal(Email, me.GetProperty("data").GetProperty("user").GetString());
        var sessions = (await client.GetFromJsonAsync<JsonElement>("/api/auth/sessions")).GetProperty("data");
        Assert.Single(sessions.EnumerateArray());
        Assert.Equal("abc", _factory.IdentityProvider.LastForm!["code"]);
    }

    [Fact]
    public async Task A_callback_with_a_tampered_or_missing_state_signs_nobody_in()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, to) = await StartAsync();
        _factory.IdentityProvider.NextToken = _factory.IdentityProvider.Issue("sub-ann", Email, true, nonce: Query(to, "nonce"));

        var tampered = await client.GetAsync("/api/auth/sso/idp/callback?code=abc&state=forged");
        Assert.Equal("/login?sso_error=bad_state", tampered.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // The proof cookie was spent by the failed attempt: replaying the right state afterwards does not work either.
        var replay = await client.GetAsync($"/api/auth/sso/idp/callback?code=abc&state={Uri.EscapeDataString(Query(to, "state"))}");
        Assert.Equal("/login?sso_error=expired", replay.Headers.Location!.OriginalString);

        // A different browser that never started has no cookie at all (login CSRF).
        var stranger = await Browser().GetAsync($"/api/auth/sso/idp/callback?code=abc&state={Uri.EscapeDataString(Query(to, "state"))}");
        Assert.Equal("/login?sso_error=expired", stranger.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_provider_saying_no_and_a_bad_token_both_end_on_the_login_page()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, to) = await StartAsync();
        var refused = await client.GetAsync("/api/auth/sso/idp/callback?error=access_denied&state=x");
        Assert.Equal("/login?sso_error=provider_refused", refused.Headers.Location!.OriginalString);

        var (client2, to2) = await StartAsync();
        _factory.IdentityProvider.NextToken = _factory.IdentityProvider.Issue("sub-ann", Email, true, nonce: Query(to2, "nonce"), audience: "other-client");
        var bad = await client2.GetAsync($"/api/auth/sso/idp/callback?code=abc&state={Uri.EscapeDataString(Query(to2, "state"))}");
        Assert.Equal("/login?sso_error=bad_token", bad.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client2.GetAsync("/api/auth/me")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example.com")]
    [InlineData("https://evil.example.com/")]
    [InlineData("/\\evil.example.com")]
    public async Task A_next_address_that_leaves_the_site_is_ignored(string next)
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, to) = await StartAsync(next);
        _factory.IdentityProvider.NextToken = _factory.IdentityProvider.Issue("sub-ann", Email, true, nonce: Query(to, "nonce"));

        var back = await client.GetAsync($"/api/auth/sso/idp/callback?code=abc&state={Uri.EscapeDataString(Query(to, "state"))}");

        Assert.Equal("/", back.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_disabled_account_is_not_let_in_and_gets_no_session()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", TestUsers.StrongPassword, ["System Manager"]);
        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Qahwa.Core.DocumentService>().UpdateAsync("User", Email, TestEngine.Values(("enabled", false)), TestUsers.Admin);
        var (client, to) = await StartAsync();
        _factory.IdentityProvider.NextToken = _factory.IdentityProvider.Issue("sub-ann", Email, true, nonce: Query(to, "nonce"));

        var back = await client.GetAsync($"/api/auth/sso/idp/callback?code=abc&state={Uri.EscapeDataString(Query(to, "state"))}");

        Assert.Equal("/login?sso_error=disabled", back.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
