using System.Security.Cryptography;
using System.Text;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class SamlTests
{
    private const string Acs = "https://qahwa.example.com/api/auth/saml/saml/acs";
    private const string Sp = "https://qahwa.example.com/sp";

    private readonly TestEngine _engine = new();

    private Task SeedAsync(string email = "ann@example.com") => _engine.Auth.EnsureUserAsync(email, "Ann Lee", "a-long-passphrase-9", ["Clerk"]);

    private static async Task<SsoException> RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<SsoException>(action);

    /// <summary>Starts a sign-in and answers it as the provider would, with the changes the test wants.</summary>
    private async Task<SsoSignIn> SignInAsync(Action<SamlResponseOptions>? change = null, string subject = "sub-1", Func<string, string>? relay = null, Func<string, string>? raw = null)
    {
        var start = _engine.Saml.Start("saml", Acs, Sp, "/app/Customer");
        var options = new SamlResponseOptions { Audience = Sp };
        change?.Invoke(options);
        var response = _engine.SamlIdp.Response(FakeSamlIdp.RequestId(start.Url), Acs, subject, options, _engine.Clock.GetUtcNow().UtcDateTime);
        return await _engine.Saml.CompleteAsync("saml", Acs, Sp, start.State, raw is null ? response : raw(response), (relay ?? (r => r))(FakeSamlIdp.RelayState(start.Url)));
    }

    [Fact]
    public void The_browser_is_sent_to_the_provider_with_a_request_and_a_relay_state()
    {
        var start = _engine.Saml.Start("saml", Acs, Sp, "/app/Customer");

        Assert.StartsWith("https://idp.example.com/sso?SAMLRequest=", start.Url);
        Assert.StartsWith("_", FakeSamlIdp.RequestId(start.Url));
        Assert.True(FakeSamlIdp.RelayState(start.Url).Length >= 32);
        Assert.DoesNotContain(FakeSamlIdp.RequestId(start.Url), start.State); // what the browser keeps is encrypted
    }

    [Fact]
    public async Task A_signed_assertion_for_an_existing_account_signs_in_and_links_the_identity()
    {
        await SeedAsync();

        var signIn = await SignInAsync();

        Assert.Equal("ann@example.com", signIn.User);
        Assert.Equal("/app/Customer", signIn.Next);
        Assert.Equal("ann@example.com", await _engine.IdentityStore.FindUserAsync(FakeSamlIdp.EntityId, "sub-1"));
        // Known by the provider's id now: a changed email at the provider does not matter.
        Assert.Equal("ann@example.com", (await SignInAsync(o => o.Email = "someone-new@example.com")).User);
    }

    [Fact]
    public async Task Signing_the_whole_response_is_just_as_good_and_the_email_may_be_the_name_id()
    {
        await SeedAsync();

        Assert.Equal("ann@example.com", (await SignInAsync(o => { o.SignAssertion = false; o.SignResponse = true; })).User);
        await SeedAsync("dan@example.com");
        Assert.Equal("dan@example.com", (await SignInAsync(o => o.UseNameIdEmail = true, subject: "dan@example.com")).User);
    }

    [Fact]
    public async Task Nothing_signed_a_foreign_key_or_a_weak_algorithm_is_refused()
    {
        await SeedAsync();

        Assert.Equal("unsigned", (await RefusedAsync(() => SignInAsync(o => o.SignAssertion = false))).Code);
        Assert.Equal("bad_signature", (await RefusedAsync(() => SignInAsync(o => o.SignWith = RSA.Create(2048)))).Code);
        Assert.Equal("bad_signature", (await RefusedAsync(() => SignInAsync(o =>
        {
            o.SignatureMethod = "http://www.w3.org/2000/09/xmldsig#rsa-sha1";
            o.DigestMethod = "http://www.w3.org/2000/09/xmldsig#sha1";
        }))).Code);
    }

    [Fact]
    public async Task Changing_the_message_after_it_was_signed_is_refused()
    {
        await SeedAsync("bob@example.com");
        await SeedAsync();

        var error = await RefusedAsync(() => SignInAsync(o => o.Email = "ann@example.com", raw: r =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Convert.FromBase64String(r)).Replace("ann@example.com", "bob@example.com")))));

        Assert.Equal("bad_signature", error.Code);
    }

    [Fact]
    public async Task A_signed_assertion_cannot_be_used_to_dress_up_an_unsigned_one_signature_wrapping()
    {
        await SeedAsync("boss@example.com");
        await SeedAsync();

        // The provider signed a harmless assertion for ann; the attacker put an unsigned one for the boss in front of it.
        var error = await RefusedAsync(() => SignInAsync(o => o.EvilSubjectFirst = "boss@example.com"));

        Assert.Equal("bad_response", error.Code);
        Assert.Null(await _engine.IdentityStore.FindUserAsync(FakeSamlIdp.EntityId, "boss@example.com"));
    }

    [Fact]
    public async Task Duplicate_ids_are_refused_so_a_signature_cannot_be_pointed_at_another_element()
    {
        await SeedAsync();

        var error = await RefusedAsync(() => SignInAsync(o => { o.EvilSubjectFirst = "boss@example.com"; o.AssertionId = "_good1"; }, raw: r => Convert.ToBase64String(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Convert.FromBase64String(r)).Replace("ID=\"_evil\"", "ID=\"_good1\"")))));

        Assert.Equal("bad_signature", error.Code);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("destination")]
    [InlineData("recipient")]
    [InlineData("in_response_to")]
    [InlineData("expired")]
    [InlineData("not_yet")]
    [InlineData("failed")]
    public async Task A_response_for_somebody_else_or_out_of_date_is_refused(string what)
    {
        await SeedAsync();
        Action<SamlResponseOptions> change = what switch
        {
            "issuer" => o => o.Issuer = "https://evil.example.com/saml",
            "audience" => o => o.Audience = "https://other-app.example.com",
            "destination" => o => o.Destination = "https://evil.example.com/acs",
            "recipient" => o => o.Recipient = "https://evil.example.com/acs",
            "in_response_to" => o => o.InResponseTo = "_somebody_elses_request",
            "expired" => o => o.NotOnOrAfter = _engine.Clock.GetUtcNow().UtcDateTime.AddHours(-1),
            "not_yet" => o => o.NotBefore = _engine.Clock.GetUtcNow().UtcDateTime.AddHours(1),
            _ => o => o.Success = false,
        };

        await RefusedAsync(() => SignInAsync(change));
        Assert.False(await _engine.Ldap.IsDirectoryUserAsync("ann@example.com"));
        Assert.Null(await _engine.IdentityStore.FindUserAsync(FakeSamlIdp.EntityId, "sub-1"));
    }

    [Fact]
    public async Task An_assertion_works_once()
    {
        await SeedAsync();
        var start = _engine.Saml.Start("saml", Acs, Sp, null);
        var response = _engine.SamlIdp.Response(FakeSamlIdp.RequestId(start.Url), Acs, "sub-1", new SamlResponseOptions { Audience = Sp }, _engine.Clock.GetUtcNow().UtcDateTime);
        var relay = FakeSamlIdp.RelayState(start.Url);

        await _engine.Saml.CompleteAsync("saml", Acs, Sp, start.State, response, relay);

        Assert.Equal("replayed", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, start.State, response, relay))).Code);
    }

    [Fact]
    public async Task A_response_needs_the_browser_that_made_the_request_and_its_relay_state()
    {
        await SeedAsync();
        var start = _engine.Saml.Start("saml", Acs, Sp, null);
        var other = _engine.Saml.Start("saml", Acs, Sp, null);
        var response = _engine.SamlIdp.Response(FakeSamlIdp.RequestId(start.Url), Acs, "sub-1", new SamlResponseOptions { Audience = Sp }, _engine.Clock.GetUtcNow().UtcDateTime);

        Assert.Equal("expired", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, null, response, FakeSamlIdp.RelayState(start.Url)))).Code); // another browser: no cookie
        Assert.Equal("expired", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, "garbage", response, FakeSamlIdp.RelayState(start.Url)))).Code);
        Assert.Equal("bad_state", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, start.State, response, FakeSamlIdp.RelayState(other.Url)))).Code);
        Assert.Equal("bad_state", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, start.State, response, null))).Code);
        Assert.Equal("bad_request_id", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, other.State, response, FakeSamlIdp.RelayState(other.Url)))).Code); // a response to someone else's request
        _engine.Clock.Advance(SamlService.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Equal("expired", (await RefusedAsync(() => _engine.Saml.CompleteAsync("saml", Acs, Sp, start.State, response, FakeSamlIdp.RelayState(start.Url)))).Code);
    }

    [Fact]
    public async Task Hostile_xml_is_refused_without_being_read_further()
    {
        await SeedAsync();
        var dtd = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>";

        Assert.Equal("bad_response", (await RefusedAsync(() => SignInAsync(o => o.Dtd = dtd))).Code);
        Assert.Equal("bad_response", (await RefusedAsync(() => SignInAsync(raw: _ => Convert.ToBase64String("<a><b/></a>"u8.ToArray()).Replace("PGE", "PGE")))).Code);
        Assert.Equal("bad_response", (await RefusedAsync(() => SignInAsync(raw: _ => "!!!not base64!!!"))).Code);
        Assert.Equal("bad_response", (await RefusedAsync(() => SignInAsync(raw: _ => Convert.ToBase64String(new byte[SamlService.MaxResponseBytes + 1])))).Code);
        Assert.Equal("no_response", (await RefusedAsync(() => SignInAsync(raw: _ => ""))).Code);
        Assert.Equal("encrypted", (await RefusedAsync(() => SignInAsync(o => o.Encrypted = true))).Code);
        Assert.Equal("bad_response", (await RefusedAsync(() => SignInAsync(raw: _ => Convert.ToBase64String("<?xml version=\"1.0\"?><x/>"u8.ToArray())))).Code);
    }

    [Fact]
    public async Task Accounts_follow_the_same_rules_as_other_single_sign_on()
    {
        // No account and no auto-create.
        Assert.Equal("no_account", (await RefusedAsync(() => SignInAsync())).Code);
        // Disabled: never switched on.
        await SeedAsync();
        await _engine.Documents.UpdateAsync("User", "ann@example.com", TestEngine.Values(("enabled", false)), TestUsers.Admin);
        Assert.Equal("disabled", (await RefusedAsync(() => SignInAsync())).Code);
        // Another provider id for an account that is already linked: refused.
        await SeedAsync("cy@example.com");
        await SignInAsync(o => o.Email = "cy@example.com", subject: "sub-cy");
        Assert.Equal("already_linked", (await RefusedAsync(() => SignInAsync(o => o.Email = "cy@example.com", subject: "sub-impostor"))).Code);
    }

    [Fact]
    public async Task People_can_be_made_for_allowed_domains_with_only_the_configured_roles()
    {
        var provider = _engine.SamlOptions.Providers["saml"];
        provider.AutoCreate = true;
        provider.AllowedDomains = ["example.com"];
        provider.DefaultRoles = ["Clerk"];

        Assert.Equal("new@example.com", (await SignInAsync(o => { o.Email = "new@example.com"; o.Name = "New Person"; }, subject: "s-new")).User);
        Assert.Equal(["All", "Clerk"], (await _engine.Auth.LoadSessionAsync("new@example.com"))!.User.Roles.Order());
        Assert.Equal("domain_not_allowed", (await RefusedAsync(() => SignInAsync(o => o.Email = "x@evil.example", subject: "s-evil"))).Code);
    }

    [Fact]
    public void Providers_are_listed_and_odd_names_are_unknown()
    {
        Assert.Equal([("saml", "Company SAML")], _engine.Saml.Providers());
        foreach (var name in new[] { "nope", "", "../x", new string('a', 50) })
            Assert.Equal("unknown_provider", Assert.Throws<SsoException>(() => _engine.Saml.Start(name, Acs, Sp, null)).Code);
    }

    [Theory]
    [InlineData("http://idp.example.com/sso", "https SsoUrl")]
    [InlineData("https://user:pw@idp.example.com/sso", "https SsoUrl")]
    [InlineData("", "https SsoUrl")]
    public void An_insecure_or_incomplete_provider_is_reported_at_start_up(string ssoUrl, string expected)
    {
        var options = new SamlOptions { Providers = { ["p"] = new SamlProvider { IdpEntityId = "x", SsoUrl = ssoUrl, Certificates = ["AAAA"] } } };

        Assert.Contains(expected, SamlService.Problem(options));
        Assert.Null(SamlService.Problem(new SamlOptions()));
    }

    [Fact]
    public void A_certificate_that_is_not_one_and_auto_create_without_domains_are_reported()
    {
        var good = _engine.SamlIdp.CertificateText;

        Assert.Contains("certificate", SamlService.Problem(new SamlOptions { Providers = { ["p"] = new SamlProvider { IdpEntityId = "x", SsoUrl = "https://i/sso", Certificates = ["not a certificate"] } } }));
        Assert.Contains("AllowedDomains", SamlService.Problem(new SamlOptions { Providers = { ["p"] = new SamlProvider { IdpEntityId = "x", SsoUrl = "https://i/sso", Certificates = [good], AutoCreate = true } } }));
        Assert.Null(SamlService.Problem(new SamlOptions { Providers = { ["p"] = new SamlProvider { IdpEntityId = "x", SsoUrl = "https://i/sso", Certificates = [good] } } }));
    }
}
