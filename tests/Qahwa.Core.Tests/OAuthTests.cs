using System.Security.Cryptography;
using System.Text;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class OAuthTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Boss = new("boss@example.com", ["System Manager"]);
    private const string Redirect = "https://app.example.com/callback";
    private static readonly string Verifier = new('v', 60);

    private static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<OAuthClientCreated> ClientAsync(bool isPublic = false, string scopes = "profile api api:write") =>
        await _engine.OAuth.CreateClientAsync(new OAuthClientRequest("Reports app", Redirect, scopes.Split(' '), isPublic), Boss);

    private async Task<string> CodeAsync(OAuthClientCreated client, string? scope = "profile api", string? verifier = null, UserContext? user = null)
    {
        var request = new OAuthAuthorizeRequest(client.Info.ClientId, Redirect, scope, "xyz", Challenge(verifier ?? Verifier), "S256");
        var back = await _engine.OAuth.DecideAsync(request, true, user ?? Ann);
        Assert.StartsWith(Redirect + "?code=", back);
        Assert.EndsWith("&state=xyz", back);
        var start = back.IndexOf("code=", StringComparison.Ordinal) + 5;
        return Uri.UnescapeDataString(back[start..back.IndexOf('&', start)]);
    }

    private Task<OAuthTokenResponse> ExchangeAsync(OAuthClientCreated client, string code, string? verifier = null, string? redirect = Redirect, bool withSecret = true, string? secret = null) =>
        _engine.OAuth.TokenAsync("authorization_code", client.Info.ClientId, withSecret ? client.ClientSecret : secret, code, redirect, verifier ?? Verifier, null);

    public OAuthTests() => _engine.Auth.EnsureUserAsync(Ann.User, "Ann Lee", TestUsers.StrongPassword, ["Clerk"]).GetAwaiter().GetResult();

    [Fact]
    public async Task The_whole_flow_gives_a_program_a_token_that_stands_for_the_person_and_no_more()
    {
        var client = await ClientAsync();
        var consent = await _engine.OAuth.ConsentAsync(new OAuthAuthorizeRequest(client.Info.ClientId, Redirect, "profile api", null, Challenge(Verifier), "S256"));
        Assert.Equal("Reports app", consent.ClientName);
        Assert.Equal(["profile", "api"], consent.Scopes.Select(s => s.Name));

        var tokens = await ExchangeAsync(client, await CodeAsync(client));

        Assert.Equal(("Bearer", 3600), (tokens.TokenType, tokens.ExpiresIn));
        var identity = (await _engine.OAuth.AuthenticateAsync(tokens.AccessToken))!;
        Assert.Equal("ann@example.com", identity.User.User);
        Assert.True(identity.CanUseApi);
        Assert.False(identity.CanWrite);
        Assert.Equal("Ann Lee", (await _engine.OAuth.UserInfoAsync(identity)).Name);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(tokens.RefreshToken)); // a refresh token is not an access token
        Assert.Null(await _engine.OAuth.AuthenticateAsync("qo_garbage"));
    }

    [Fact]
    public async Task A_code_works_once_and_using_it_again_withdraws_what_it_produced()
    {
        var client = await ClientAsync();
        var code = await CodeAsync(client);
        var tokens = await ExchangeAsync(client, code);

        var again = await Assert.ThrowsAsync<OAuthException>(() => ExchangeAsync(client, code));

        Assert.Equal("invalid_grant", again.Error);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(tokens.AccessToken));
    }

    [Fact]
    public async Task The_code_must_be_exchanged_by_the_same_client_with_the_same_address_and_verifier()
    {
        var client = await ClientAsync();
        var other = await ClientAsync();

        Assert.Equal("invalid_grant", (await Assert.ThrowsAsync<OAuthException>(() => ExchangeAsync(client, "qc_nonsense"))).Error);
        Assert.Equal("invalid_grant", (await Assert.ThrowsAsync<OAuthException>(async () => await ExchangeAsync(client, await CodeAsync(client), verifier: new string('w', 60)))).Error);
        Assert.Equal("invalid_grant", (await Assert.ThrowsAsync<OAuthException>(async () => await ExchangeAsync(client, await CodeAsync(client), redirect: "https://evil.example.com/cb"))).Error);
        Assert.Equal("invalid_grant", (await Assert.ThrowsAsync<OAuthException>(async () => await _engine.OAuth.TokenAsync("authorization_code", other.Info.ClientId, other.ClientSecret, await CodeAsync(client), Redirect, Verifier, null))).Error);
        Assert.Equal("invalid_client", (await Assert.ThrowsAsync<OAuthException>(async () => await ExchangeAsync(client, await CodeAsync(client), withSecret: false, secret: "wrong"))).Error);
        Assert.Equal("invalid_client", (await Assert.ThrowsAsync<OAuthException>(async () => await ExchangeAsync(client, await CodeAsync(client), withSecret: false))).Error);
    }

    [Fact]
    public async Task A_code_that_has_expired_is_refused()
    {
        var client = await ClientAsync();
        var code = await CodeAsync(client);

        ((TestClock)_engine.Clock).Advance(OAuthService.CodeLifetime + TimeSpan.FromSeconds(1));

        Assert.Equal("invalid_grant", (await Assert.ThrowsAsync<OAuthException>(() => ExchangeAsync(client, code))).Error);
    }

    [Fact]
    public async Task Requests_that_could_leak_a_code_are_refused_before_anything_is_sent_anywhere()
    {
        var client = await ClientAsync();
        var challenge = Challenge(Verifier);

        await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.DecideAsync(new(client.Info.ClientId, "https://evil.example.com/cb", "profile", null, challenge, "S256"), true, Ann)); // not registered
        await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.DecideAsync(new(client.Info.ClientId, Redirect + "/", "profile", null, challenge, "S256"), true, Ann)); // only an exact match
        await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.DecideAsync(new(client.Info.ClientId, Redirect, "profile", null, null, null), true, Ann)); // PKCE is required
        await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.DecideAsync(new(client.Info.ClientId, Redirect, "profile", null, challenge, "plain"), true, Ann));
        await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.DecideAsync(new("qa_nobody", Redirect, "profile", null, challenge, "S256"), true, Ann));
        var scope = await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.DecideAsync(new(client.Info.ClientId, Redirect, "profile admin", null, challenge, "S256"), true, Ann));
        Assert.Equal("invalid_scope", scope.Error);
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.OAuth.DecideAsync(new(client.Info.ClientId, Redirect, "profile", null, challenge, "S256"), true, UserContext.Anonymous));
    }

    [Fact]
    public async Task Saying_no_sends_the_person_back_with_an_error_and_the_state_and_no_code()
    {
        var client = await ClientAsync();

        var back = await _engine.OAuth.DecideAsync(new(client.Info.ClientId, Redirect, "profile", "a b&c", Challenge(Verifier), "S256"), false, Ann);

        Assert.Equal(Redirect + "?error=access_denied&state=a%20b%26c", back);
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("OAuth Token"), new ListQuery { Limit = 5 }));
    }

    [Fact]
    public async Task Refresh_tokens_rotate_and_a_used_one_withdraws_the_whole_family()
    {
        var client = await ClientAsync();
        var first = await ExchangeAsync(client, await CodeAsync(client));

        var second = await _engine.OAuth.TokenAsync("refresh_token", client.Info.ClientId, client.ClientSecret, null, null, null, first.RefreshToken);
        Assert.NotEqual(first.AccessToken, second.AccessToken);
        Assert.NotNull(await _engine.OAuth.AuthenticateAsync(second.AccessToken));

        var reuse = await Assert.ThrowsAsync<OAuthException>(() => _engine.OAuth.TokenAsync("refresh_token", client.Info.ClientId, client.ClientSecret, null, null, null, first.RefreshToken));

        Assert.Equal("invalid_grant", reuse.Error);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(second.AccessToken)); // the leak is assumed; nothing from this approval works any more
    }

    [Fact]
    public async Task Access_tokens_expire_and_a_public_client_needs_no_secret()
    {
        var client = await ClientAsync(isPublic: true);
        Assert.Null(client.ClientSecret);
        var tokens = await ExchangeAsync(client, await CodeAsync(client), withSecret: false);
        Assert.NotNull(await _engine.OAuth.AuthenticateAsync(tokens.AccessToken));

        ((TestClock)_engine.Clock).Advance(OAuthService.AccessLifetime + TimeSpan.FromSeconds(1));

        Assert.Null(await _engine.OAuth.AuthenticateAsync(tokens.AccessToken));
        Assert.NotNull(await _engine.OAuth.TokenAsync("refresh_token", client.Info.ClientId, null, null, null, null, tokens.RefreshToken)); // still within the refresh lifetime
    }

    [Fact]
    public async Task Revoking_disconnecting_deleting_the_client_or_disabling_the_person_ends_the_access()
    {
        var client = await ClientAsync();
        var a = await ExchangeAsync(client, await CodeAsync(client));
        await _engine.OAuth.RevokeAsync(client.Info.ClientId, client.ClientSecret, a.AccessToken);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(a.AccessToken));

        var b = await ExchangeAsync(client, await CodeAsync(client));
        Assert.Equal("Reports app", Assert.Single(await _engine.OAuth.ConnectedAsync(Ann)).Name);
        await _engine.OAuth.DisconnectAsync(client.Info.ClientId, Ann);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(b.AccessToken));
        Assert.Empty(await _engine.OAuth.ConnectedAsync(Ann));

        var c = await ExchangeAsync(client, await CodeAsync(client));
        await _engine.OAuth.DeleteClientAsync(client.Info.Id, Boss);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(c.AccessToken));

        var other = await ClientAsync();
        var d = await ExchangeAsync(other, await CodeAsync(other));
        await _engine.Documents.UpdateAsync("User", Ann.User, new Dictionary<string, object?> { ["enabled"] = false }, TestUsers.Admin);
        Assert.Null(await _engine.OAuth.AuthenticateAsync(d.AccessToken));
    }

    [Fact]
    public async Task A_token_cannot_be_revoked_with_another_applications_credentials_and_unknown_tokens_say_nothing()
    {
        var mine = await ClientAsync();
        var theirs = await ClientAsync();
        var token = await ExchangeAsync(theirs, await CodeAsync(theirs));

        await _engine.OAuth.RevokeAsync(mine.Info.ClientId, mine.ClientSecret, token.AccessToken);
        await _engine.OAuth.RevokeAsync(mine.Info.ClientId, mine.ClientSecret, "qo_unknown");

        Assert.NotNull(await _engine.OAuth.AuthenticateAsync(token.AccessToken));
    }

    [Fact]
    public async Task Only_hashes_are_stored_and_only_a_system_manager_registers_clients_with_safe_redirect_addresses()
    {
        var client = await ClientAsync();
        var code = await CodeAsync(client);
        await ExchangeAsync(client, code);

        var rows = await _engine.Repository.ListAsync(_engine.Registry.Get("OAuth Token"), new ListQuery { Limit = 10 });
        Assert.All(rows, r => Assert.DoesNotContain(code, string.Join("|", r.Values.Select(v => v?.ToString()))));
        var stored = (await _engine.Repository.ListAsync(_engine.Registry.Get("OAuth Client"), new ListQuery { Limit = 1 }))[0];
        Assert.NotEqual(client.ClientSecret, stored["secret_hash"]);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.OAuth.CreateClientAsync(new("x", Redirect, null), Ann));
        foreach (var bad in new[] { "http://app.example.com/cb", "javascript:alert(1)", "https://u:p@app.example.com/cb", "https://app.example.com/cb#frag", "ftp://x/y", "/relative", "data:text/html,x" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.OAuth.CreateClientAsync(new("x", bad, null), Boss));
        foreach (var good in new[] { "http://localhost:8080/cb", "http://127.0.0.1/cb", "com.example.app:/done", "https://app.example.com/cb?x=1" })
            Assert.Null(OAuthService.RedirectProblem(good));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.OAuth.CreateClientAsync(new("x", Redirect, ["profile", "root"]), Boss));
    }
}
