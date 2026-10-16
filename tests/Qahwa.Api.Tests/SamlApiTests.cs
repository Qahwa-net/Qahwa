using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class SamlApiTests : IAsyncDisposable
{
    private const string Acs = "http://localhost/api/auth/saml/saml/acs";
    private const string Entity = "http://localhost/api/auth/saml/saml/metadata";

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private HttpClient Browser() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private async Task<(HttpClient Client, string ToProvider)> StartAsync(string next = "/app/Customer")
    {
        var client = Browser();
        var response = await client.GetAsync("/api/auth/saml/saml/login?next=" + Uri.EscapeDataString(next));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (client, response.Headers.Location!.OriginalString);
    }

    private Task<HttpResponseMessage> PostAsync(HttpClient client, string response, string relay) =>
        client.PostAsync("/api/auth/saml/saml/acs", new FormUrlEncodedContent(new Dictionary<string, string> { ["SAMLResponse"] = response, ["RelayState"] = relay }));

    private string Answer(string toProvider, Action<SamlResponseOptions>? change = null, string subject = "sub-ann")
    {
        var options = new SamlResponseOptions { Audience = Entity };
        change?.Invoke(options);
        return _factory.SamlIdp.Response(FakeSamlIdp.RequestId(toProvider), Acs, subject, options);
    }

    [Fact]
    public async Task The_login_page_can_list_the_providers_without_secrets()
    {
        var list = (await Browser().GetFromJsonAsync<JsonElement>("/api/auth/saml")).GetProperty("data");

        Assert.Equal("saml", list[0].GetProperty("name").GetString());
        Assert.Equal("Company SAML", list[0].GetProperty("display_name").GetString());
        Assert.DoesNotContain("Certificate", list.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Starting_sends_the_browser_to_the_provider_and_keeps_the_proof_in_a_private_cookie()
    {
        var response = await Browser().GetAsync("/api/auth/saml/saml/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://idp.example.com/sso?SAMLRequest=", response.Headers.Location!.OriginalString);
        var cookie = response.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("qahwa.saml=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/api/auth/saml", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_whole_round_trip_signs_the_person_in_and_lands_on_the_page_they_wanted()
    {
        await _factory.Auth.EnsureUserAsync("ann@example.com", "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, toProvider) = await StartAsync("/app/Customer");

        var back = await PostAsync(client, Answer(toProvider), FakeSamlIdp.RelayState(toProvider));

        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/app/Customer", back.Headers.Location!.OriginalString);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("ann@example.com", me.GetProperty("data").GetProperty("user").GetString());
        Assert.Single((await client.GetFromJsonAsync<JsonElement>("/api/auth/sessions")).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task Forged_foreign_unsigned_and_replayed_answers_sign_nobody_in()
    {
        await _factory.Auth.EnsureUserAsync("ann@example.com", "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, toProvider) = await StartAsync();
        var relay = FakeSamlIdp.RelayState(toProvider);

        var wrongKey = await PostAsync(client, Answer(toProvider, o => o.SignWith = System.Security.Cryptography.RSA.Create(2048)), relay);
        Assert.Equal("/login?sso_error=bad_signature", wrongKey.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);

        // The one-time cookie was spent by that attempt: even a perfect answer afterwards is too late.
        var late = await PostAsync(client, Answer(toProvider), relay);
        Assert.Equal("/login?sso_error=expired", late.Headers.Location!.OriginalString);

        // Another browser that never started has no cookie (login CSRF).
        var (_, other) = await StartAsync();
        var stranger = await PostAsync(Browser(), Answer(other), FakeSamlIdp.RelayState(other));
        Assert.Equal("/login?sso_error=expired", stranger.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("//evil.example.com")]
    [InlineData("https://evil.example.com/")]
    public async Task A_next_address_that_leaves_the_site_is_ignored(string next)
    {
        await _factory.Auth.EnsureUserAsync("ann@example.com", "Ann", TestUsers.StrongPassword, ["System Manager"]);
        var (client, toProvider) = await StartAsync(next);

        var back = await PostAsync(client, Answer(toProvider), FakeSamlIdp.RelayState(toProvider));

        Assert.Equal("/", back.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Odd_posts_are_turned_back_with_a_short_code()
    {
        var client = Browser();
        var noForm = await client.PostAsync("/api/auth/saml/saml/acs", new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));
        var unknown = await client.GetAsync("/api/auth/saml/nope/login");

        Assert.StartsWith("/login?sso_error=", noForm.Headers.Location!.OriginalString);
        Assert.Equal("/login?sso_error=unknown_provider", unknown.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_metadata_tells_the_provider_where_to_post()
    {
        var response = await Browser().GetAsync("/api/auth/saml/saml/metadata");
        var xml = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"entityID=\"{Entity}\"", xml);
        Assert.Contains($"Location=\"{Acs}\"", xml);
        Assert.Equal(HttpStatusCode.NotFound, (await Browser().GetAsync("/api/auth/saml/nope/metadata")).StatusCode);
    }
}
