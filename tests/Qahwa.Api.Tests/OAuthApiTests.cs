using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class OAuthApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new(realAuth: true);
    private const string Redirect = "https://app.example.com/callback";

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task<HttpClient> SignedInAsync(string email, params string[] roles)
    {
        await _factory.Auth.EnsureUserAsync(email, email.Split('@')[0], TestUsers.StrongPassword, roles);
        var client = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email, password = TestUsers.StrongPassword })).StatusCode);
        return client;
    }

    private HttpClient Program(string? bearer = null)
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { HandleCookies = false });
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private static FormUrlEncodedContent Form(params (string, string)[] pairs) => new(pairs.ToDictionary(p => p.Item1, p => p.Item2));

    [Fact]
    public async Task A_program_is_given_access_through_the_flow_and_is_limited_to_what_the_person_approved()
    {
        var ann = await SignedInAsync("ann@example.com", "Sales Manager");
        var boss = await SignedInAsync("boss@example.com", "System Manager");

        Assert.Equal(HttpStatusCode.Forbidden, (await ann.PostAsJsonAsync("/api/oauth/clients", new { name = "App", redirectUris = Redirect })).StatusCode);
        var created = (await JsonOf(await boss.PostAsJsonAsync("/api/oauth/clients", new { name = "Reports app", redirectUris = Redirect, scopes = new[] { "profile", "api" } }))).GetProperty("data");
        var clientId = created.GetProperty("info").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("clientSecret").GetString()!;

        var verifier = new string('k', 64);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var query = $"client_id={clientId}&redirect_uri={Uri.EscapeDataString(Redirect)}&scope=profile%20api&state=s1&code_challenge={challenge}&code_challenge_method=S256";
        Assert.Equal("Reports app", (await JsonOf(await ann.GetAsync("/api/oauth/authorize?" + query))).GetProperty("data").GetProperty("clientName").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.GetAsync($"/api/oauth/authorize?client_id={clientId}&redirect_uri=https%3A%2F%2Fevil.example.com&scope=profile&code_challenge={challenge}&code_challenge_method=S256")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Program().GetAsync("/api/oauth/authorize?" + query)).StatusCode);

        var decided = await JsonOf(await ann.PostAsJsonAsync("/api/oauth/authorize", new { clientId, redirectUri = Redirect, scope = "profile api", state = "s1", codeChallenge = challenge, codeChallengeMethod = "S256", approve = true }));
        var back = new Uri(decided.GetProperty("data").GetProperty("redirect").GetString()!);
        var code = System.Web.HttpUtility.ParseQueryString(back.Query)["code"]!;
        Assert.Equal("s1", System.Web.HttpUtility.ParseQueryString(back.Query)["state"]);

        var program = Program();
        var token = await program.PostAsync("/api/oauth/token", Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("code_verifier", verifier), ("client_id", clientId), ("client_secret", secret)));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Equal("no-store", token.Headers.CacheControl?.ToString());
        var tokens = await JsonOf(token);
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal("Bearer", tokens.GetProperty("token_type").GetString());

        var acting = Program(access);
        Assert.Equal("ann@example.com", (await JsonOf(await acting.GetAsync("/api/oauth/userinfo"))).GetProperty("email").GetString());
        Assert.Equal(HttpStatusCode.OK, (await acting.GetAsync("/api/resource/Customer")).StatusCode); // reading is allowed with the api permission
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Sneaky" })).StatusCode); // no api:write
        Assert.Equal(HttpStatusCode.Forbidden, (await acting.PostAsJsonAsync("/api/oauth/clients", new { name = "X", redirectUris = Redirect })).StatusCode); // a token is not a signed-in person
        Assert.Equal(HttpStatusCode.Unauthorized, (await Program().GetAsync("/api/oauth/userinfo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Program("qo_forged").GetAsync("/api/resource/Customer")).StatusCode);

        Assert.Equal("Reports app", (await JsonOf(await ann.GetAsync("/api/oauth/connected"))).GetProperty("data")[0].GetProperty("name").GetString());
        Assert.EndsWith("/api/oauth/token", (await JsonOf(await program.GetAsync("/api/oauth/metadata"))).GetProperty("token_endpoint").GetString());

        // Using the same code again is refused and withdraws what it produced.
        var again = await program.PostAsync("/api/oauth/token", Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("code_verifier", verifier), ("client_id", clientId), ("client_secret", secret)));
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_grant", (await JsonOf(again)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await acting.GetAsync("/api/oauth/userinfo")).StatusCode);
    }

    [Fact]
    public async Task A_person_can_take_back_access_and_a_client_with_a_bad_secret_gets_nothing()
    {
        var ann = await SignedInAsync("ann@example.com", "Sales Manager");
        var boss = await SignedInAsync("boss@example.com", "System Manager");
        var created = (await JsonOf(await boss.PostAsJsonAsync("/api/oauth/clients", new { name = "Writer", redirectUris = Redirect, scopes = new[] { "profile", "api", "api:write" } }))).GetProperty("data");
        var clientId = created.GetProperty("info").GetProperty("clientId").GetString()!;
        var secret = created.GetProperty("clientSecret").GetString()!;
        var verifier = new string('z', 64);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var decided = await JsonOf(await ann.PostAsJsonAsync("/api/oauth/authorize", new { clientId, redirectUri = Redirect, scope = "profile api api:write", state = "x", codeChallenge = challenge, codeChallengeMethod = "S256", approve = true }));
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(decided.GetProperty("data").GetProperty("redirect").GetString()!).Query)["code"]!;

        var wrong = await Program().PostAsync("/api/oauth/token", Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("code_verifier", verifier), ("client_id", clientId), ("client_secret", "not-the-secret")));
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalid_client", (await JsonOf(wrong)).GetProperty("error").GetString());

        var basic = Program();
        basic.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{secret}")));
        var ok = await basic.PostAsync("/api/oauth/token", Form(("grant_type", "authorization_code"), ("code", code), ("redirect_uri", Redirect), ("code_verifier", verifier)));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var access = (await JsonOf(ok)).GetProperty("access_token").GetString()!;

        var acting = Program(access);
        Assert.Equal(HttpStatusCode.Created, (await acting.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Made by a program" })).StatusCode); // api:write was approved

        Assert.Equal(HttpStatusCode.OK, (await ann.DeleteAsync($"/api/oauth/connected/{clientId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await acting.GetAsync("/api/resource/Customer")).StatusCode);
    }
}
