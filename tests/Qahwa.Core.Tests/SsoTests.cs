using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class SsoTests
{
    private const string Redirect = "https://qahwa.example.com/api/auth/sso/idp/callback";
    private const string Password = "a-long-passphrase-9";

    private readonly TestEngine _engine = new();

    private static string Query(string url, string name) =>
        Uri.UnescapeDataString(url[(url.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1)..].Split('&')[0]);

    /// <summary>Does the first half (the browser goes to the provider) and returns what a provider would know about it.</summary>
    private async Task<(SsoStart Start, string State, string Nonce)> StartAsync(string? next = "/app/Customer")
    {
        var start = await _engine.Sso.StartAsync("idp", Redirect, next);
        return (start, Query(start.Url, "state"), Query(start.Url, "nonce"));
    }

    private async Task<SsoSignIn> SignInAsync(string subject, string? email, bool? verified = true, string? name = null, Action<(SsoStart Start, string State, string Nonce)>? tamper = null)
    {
        var s = await StartAsync();
        tamper?.Invoke(s);
        _engine.IdentityProvider.NextToken = _engine.IdentityProvider.Issue(subject, email, verified, name, s.Nonce);
        return await _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "code-1", s.State);
    }

    private async Task<SsoException> RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<SsoException>(action);

    private Task SeedAsync(string email = "ann@example.com", params string[] roles) => _engine.Auth.EnsureUserAsync(email, "Ann Lee", Password, roles);

    [Fact]
    public async Task The_browser_is_sent_to_the_provider_with_state_nonce_and_a_pkce_challenge()
    {
        var (start, state, nonce) = await StartAsync();

        Assert.StartsWith("https://idp.example.com/authorize?", start.Url);
        Assert.Equal("code", Query(start.Url, "response_type"));
        Assert.Equal(FakeIdentityProvider.ClientId, Query(start.Url, "client_id"));
        Assert.Equal(Redirect, Query(start.Url, "redirect_uri"));
        Assert.Equal("S256", Query(start.Url, "code_challenge_method"));
        Assert.Equal("openid email profile", Query(start.Url, "scope"));
        Assert.True(state.Length >= 30 && nonce.Length >= 30);
        Assert.NotEqual(state, nonce);
        Assert.DoesNotContain(state, start.State); // what the browser keeps is encrypted, not the state itself
    }

    [Fact]
    public async Task An_existing_account_is_linked_by_its_verified_email_and_then_known_by_the_providers_own_id()
    {
        await SeedAsync("ann@example.com", "Clerk");

        var first = await SignInAsync("sub-1", "Ann@Example.com");
        var form = _engine.IdentityProvider.LastForm!;

        Assert.Equal("ann@example.com", first.User);
        Assert.Equal("/app/Customer", first.Next);
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal(Redirect, form["redirect_uri"]);
        Assert.True(form["code_verifier"].Length >= 43); // PKCE
        Assert.Equal("s3cret", _engine.IdentityProvider.LastClientSecret);

        var again = await SignInAsync("sub-1", "a-different-address@example.com"); // the email changed at the provider: still her
        Assert.Equal("ann@example.com", again.User);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task An_email_the_provider_does_not_vouch_for_is_refused(bool? verified)
    {
        await SeedAsync();

        var error = await RefusedAsync(() => SignInAsync("sub-1", "ann@example.com", verified));

        Assert.Equal("email_not_verified", error.Code);
    }

    [Fact]
    public async Task A_provider_that_never_sends_the_flag_can_be_trusted_by_choice()
    {
        await SeedAsync();
        _engine.SsoOptions.Providers["idp"].TrustEmail = true;

        Assert.Equal("ann@example.com", (await SignInAsync("sub-1", "ann@example.com", verified: null)).User);
    }

    [Fact]
    public async Task Someone_else_with_the_same_email_but_another_id_cannot_take_over_a_linked_account()
    {
        await SeedAsync();
        await SignInAsync("sub-real", "ann@example.com");

        var error = await RefusedAsync(() => SignInAsync("sub-impostor", "ann@example.com"));

        Assert.Equal("already_linked", error.Code);
    }

    [Fact]
    public async Task A_disabled_account_is_never_switched_back_on_and_unknown_people_need_AutoCreate()
    {
        await SeedAsync();
        await _engine.Documents.UpdateAsync("User", "ann@example.com", TestEngine.Values(("enabled", false)), TestUsers.Admin);

        Assert.Equal("disabled", (await RefusedAsync(() => SignInAsync("sub-1", "ann@example.com"))).Code);
        Assert.Equal("no_account", (await RefusedAsync(() => SignInAsync("sub-2", "nobody@example.com"))).Code);
        Assert.Equal(AccountState.None, await _engine.Auth.AccountStateAsync("nobody@example.com"));
        Assert.Equal(AccountState.Disabled, await _engine.Auth.AccountStateAsync("ann@example.com"));
    }

    [Fact]
    public async Task A_disabled_account_stays_refused_even_when_it_was_linked_before()
    {
        await SeedAsync();
        await SignInAsync("sub-1", "ann@example.com");
        await _engine.Documents.UpdateAsync("User", "ann@example.com", TestEngine.Values(("enabled", false)), TestUsers.Admin);

        Assert.Equal("disabled", (await RefusedAsync(() => SignInAsync("sub-1", "ann@example.com"))).Code);
    }

    [Fact]
    public async Task Accounts_can_be_made_on_the_fly_only_for_allowed_domains_and_get_only_the_configured_roles()
    {
        var provider = _engine.SsoOptions.Providers["idp"];
        provider.AutoCreate = true;
        provider.DefaultRoles = ["Clerk"];

        // No allowed domains: nothing is created (anyone with any email would otherwise get an account).
        Assert.Equal("no_account", (await RefusedAsync(() => SignInAsync("s1", "new@example.com"))).Code);

        provider.AllowedDomains = ["example.com"];
        var made = await SignInAsync("s2", "new@example.com", name: "New Person");
        var session = (await _engine.Auth.LoadSessionAsync(made.User))!;
        Assert.Equal("New Person", session.FullName);
        Assert.Equal(["All", "Clerk"], session.User.Roles.Order());
        Assert.DoesNotContain(UserContext.AdministratorRole, session.User.Roles);

        Assert.Equal("domain_not_allowed", (await RefusedAsync(() => SignInAsync("s3", "intruder@evil.example"))).Code);
        Assert.Equal("domain_not_allowed", (await RefusedAsync(() => SignInAsync("s4", "x@example.com.evil.example"))).Code);
    }

    [Fact]
    public async Task The_provider_cannot_give_roles_through_the_token()
    {
        await SeedAsync("ann@example.com", "Clerk");
        var s = await StartAsync();
        var token = _engine.IdentityProvider.Issue("sub-1", "ann@example.com", true, nonce: s.Nonce);
        _engine.IdentityProvider.NextToken = token;

        await _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "c", s.State);

        Assert.Equal(["All", "Clerk"], (await _engine.Auth.LoadSessionAsync("ann@example.com"))!.User.Roles.Order());
    }

    [Fact]
    public async Task A_state_that_does_not_match_or_that_was_never_started_here_is_refused()
    {
        await SeedAsync();
        var s = await StartAsync();
        _engine.IdentityProvider.NextToken = _engine.IdentityProvider.Issue("sub-1", "ann@example.com", true, nonce: s.Nonce);

        Assert.Equal("bad_state", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "c", "somebody-elses-state"))).Code);
        Assert.Equal("bad_state", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "c", null))).Code);
        Assert.Equal("expired", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, null, "c", s.State))).Code); // no cookie: a forged callback
        Assert.Equal("expired", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, "garbage", "c", s.State))).Code);
        Assert.Equal("no_code", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "", s.State))).Code);
    }

    [Fact]
    public async Task The_saved_state_expires_and_is_tied_to_its_provider()
    {
        await SeedAsync();
        var s = await StartAsync();
        _engine.IdentityProvider.NextToken = _engine.IdentityProvider.Issue("sub-1", "ann@example.com", true, nonce: s.Nonce);
        _engine.SsoOptions.Providers["other"] = new SsoProvider { Authority = FakeIdentityProvider.Authority, ClientId = FakeIdentityProvider.ClientId };

        Assert.Equal("expired", (await RefusedAsync(() => _engine.Sso.CompleteAsync("other", Redirect, s.Start.State, "c", s.State))).Code);
        _engine.Clock.Advance(SsoService.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal("expired", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "c", s.State))).Code);
    }

    [Fact]
    public async Task A_token_that_is_wrong_in_any_way_is_refused()
    {
        await SeedAsync();
        async Task<string> Code(Func<string, string> issue)
        {
            var s = await StartAsync();
            _engine.IdentityProvider.NextToken = issue(s.Nonce);
            return (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "c", s.State))).Code;
        }

        var p = _engine.IdentityProvider;
        Assert.Equal("bad_nonce", await Code(n => p.Issue("sub-1", "ann@example.com", true, nonce: "not-the-nonce")));
        Assert.Equal("bad_nonce", await Code(n => p.Issue("sub-1", "ann@example.com", true, nonce: null)));
        Assert.Equal("bad_token", await Code(n => p.Issue("sub-1", "ann@example.com", true, nonce: n, audience: "someone-elses-client")));
        Assert.Equal("bad_token", await Code(n => p.Issue("sub-1", "ann@example.com", true, nonce: n, issuer: "https://evil.example.com")));
        Assert.Equal("bad_token", await Code(n => p.Issue("sub-1", "ann@example.com", true, nonce: n, now: DateTime.UtcNow.AddHours(-2)))); // long expired
        Assert.Equal("bad_token", await Code(n => p.Issue("sub-1", "ann@example.com", true, nonce: n, signWith: RSA.Create(2048)))); // signed by someone else
        Assert.Equal("bad_token", await Code(n => "not.a.token"));
        Assert.Equal("bad_token", await Code(n => new string('a', 30_000)));
        Assert.Equal("bad_token", await Code(n => "eyJhbGciOiJub25lIiwidHlwIjoiSldUIn0." + Convert.ToBase64String("""{"sub":"x","iss":"https://idp.example.com","aud":"qahwa-client","exp":9999999999}"""u8.ToArray()).TrimEnd('=') + ".")); // "alg: none"
        p.NextToken = null;
        var s2 = await StartAsync();
        Assert.Equal("no_token", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s2.Start.State, "c", s2.State))).Code);
    }

    [Fact]
    public async Task A_shared_secret_signature_cannot_be_used_in_place_of_the_providers_key()
    {
        await SeedAsync();
        var s = await StartAsync();
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = FakeIdentityProvider.Authority, Audience = FakeIdentityProvider.ClientId, Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { ["sub"] = "sub-1", ["email"] = "ann@example.com", ["email_verified"] = true, ["nonce"] = s.Nonce },
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("s3cret-s3cret-s3cret-s3cret-s3cret")), SecurityAlgorithms.HmacSha256),
        };
        _engine.IdentityProvider.NextToken = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(descriptor);

        Assert.Equal("bad_token", (await RefusedAsync(() => _engine.Sso.CompleteAsync("idp", Redirect, s.Start.State, "c", s.State))).Code);
    }

    [Fact]
    public async Task A_provider_that_rotated_its_key_is_followed_by_asking_for_the_keys_again()
    {
        await SeedAsync();
        _engine.IdentityProvider.Rotate(); // the first key set we are given is stale
        var fetchesBefore = _engine.IdentityProvider.KeyFetches;

        var signIn = await SignInAsync("sub-1", "ann@example.com");

        Assert.Equal("ann@example.com", signIn.User);
        Assert.Equal(2, _engine.IdentityProvider.KeyFetches - fetchesBefore);
    }

    [Theory]
    [InlineData("/app/Customer", "/app/Customer")]
    [InlineData("/", "/")]
    [InlineData("//evil.example.com", "/")]
    [InlineData("/\\evil.example.com", "/")]
    [InlineData("https://evil.example.com", "/")]
    [InlineData("javascript:alert(1)", "/")]
    [InlineData("/ok\r\nSet-Cookie: x=1", "/")]
    [InlineData("", "/")]
    [InlineData(null, "/")]
    public void Only_a_path_on_this_site_is_ever_followed_after_signing_in(string? next, string expected) => Assert.Equal(expected, SsoService.SafeNext(next));

    [Fact]
    public async Task Providers_are_listed_by_name_and_unknown_or_odd_names_are_refused()
    {
        Assert.Equal([("idp", "Company login")], _engine.Sso.Providers());
        foreach (var name in new[] { "nope", "", "../x", "a b", new string('a', 50) })
            Assert.Equal("unknown_provider", (await RefusedAsync(() => _engine.Sso.StartAsync(name, Redirect, null))).Code);
    }

    [Fact]
    public async Task An_enabled_account_that_has_no_password_yet_can_use_single_sign_on()
    {
        await _engine.Documents.InsertAsync("User", TestEngine.Values(("email", "pat@example.com"), ("full_name", "Pat"), ("enabled", true)), TestUsers.Admin);
        Assert.Equal(AccountState.NoPassword, await _engine.Auth.AccountStateAsync("pat@example.com"));

        var signIn = await SignInAsync("sub-pat", "pat@example.com");

        Assert.Equal("pat@example.com", signIn.User);
        Assert.NotNull(await _engine.Auth.LoadSessionAsync("pat@example.com"));
        Assert.Null(await _engine.Auth.VerifyPasswordAsync("pat@example.com", "guess-guess-guess-1")); // nobody knows the placeholder password
    }
}
