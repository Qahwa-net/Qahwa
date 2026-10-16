using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class LdapTests
{
    private const string Dn = "uid=ann,ou=people,dc=example,dc=com";
    private const string Secret = "directory-pass-1";

    private readonly TestEngine _engine = new();

    public LdapTests() => _engine.LdapDirectory.Add(Dn, "ann@example.com", Secret, "Ann Lee");

    private Task<string?> SignInAsync(string? email = "ann@example.com", string? password = Secret) => _engine.Ldap.SignInAsync(email, password);

    [Fact]
    public async Task An_existing_account_signs_in_with_the_directory_password_and_is_linked()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", ["Clerk"]);

        Assert.Equal("ann@example.com", await SignInAsync("Ann@Example.com"));

        Assert.True(await _engine.Ldap.IsDirectoryUserAsync("ann@example.com"));
        Assert.Equal(Dn, _engine.LdapDirectory.Binds.Single().Dn);
    }

    [Fact]
    public async Task A_wrong_password_or_an_unknown_person_gets_nothing_and_nothing_is_linked()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);

        Assert.Null(await SignInAsync(password: "wrong-password-1"));
        Assert.Null(await SignInAsync("nobody@example.com"));
        Assert.False(await _engine.Ldap.IsDirectoryUserAsync("ann@example.com"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task An_empty_password_is_never_sent_to_the_directory(string? password)
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);

        Assert.Null(await SignInAsync(password: password));
        Assert.Empty(_engine.LdapDirectory.Binds);
        Assert.Equal(0, _engine.LdapDirectory.Searches);
    }

    [Fact]
    public async Task Odd_passwords_and_addresses_are_refused_before_the_directory_is_asked()
    {
        Assert.Null(await SignInAsync(password: new string('a', 200)));
        Assert.Null(await SignInAsync(password: "has\0nul"));
        Assert.Null(await SignInAsync("not-an-address"));
        Assert.Null(await SignInAsync(null));
        Assert.Equal(0, _engine.LdapDirectory.Searches);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*)(uid=*))(|(uid=*")]
    [InlineData("ann@example.com)(mail=*")]
    [InlineData("a\\b@example.com")]
    public async Task The_email_cannot_change_the_search(string email)
    {
        await SignInAsync(email);

        // Whatever was sent, the filter keeps its shape: the special characters arrive escaped.
        foreach (var filter in _engine.LdapDirectory.Filters)
        {
            Assert.StartsWith("(&(objectClass=person)(mail=", filter);
            Assert.EndsWith("))", filter);
            var value = filter["(&(objectClass=person)(mail=".Length..^2];
            Assert.DoesNotContain("*", value);
            Assert.DoesNotContain("(", value);
            Assert.DoesNotContain(")", value);
        }

        Assert.Empty(_engine.LdapDirectory.Binds);
    }

    [Fact]
    public void Filter_escaping_follows_the_standard()
    {
        Assert.Equal("a\\2ab\\28c\\29d\\5ce\\00f", LdapService.EscapeFilter("a*b(c)d\\e\0f"));
        Assert.Equal("plain@example.com", LdapService.EscapeFilter("plain@example.com"));
    }

    [Fact]
    public async Task Someone_found_twice_in_the_directory_cannot_sign_in()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);
        _engine.LdapDirectory.Add("uid=ann2,ou=people,dc=example,dc=com", "ann@example.com", Secret);

        Assert.Null(await SignInAsync());
    }

    [Fact]
    public async Task The_directory_entry_must_really_have_that_email()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);
        _engine.LdapDirectory.ChangeEmail(Dn, "someone-else@example.com");

        Assert.Null(await SignInAsync());
    }

    [Fact]
    public async Task A_disabled_account_is_never_switched_back_on_by_the_directory()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);
        await _engine.Documents.UpdateAsync("User", "ann@example.com", TestEngine.Values(("enabled", false)), TestUsers.Admin);

        Assert.Null(await SignInAsync());
        Assert.Equal(AccountState.Disabled, await _engine.Auth.AccountStateAsync("ann@example.com"));
    }

    [Fact]
    public async Task Unknown_people_get_an_account_only_when_allowed_and_only_the_configured_roles()
    {
        Assert.Null(await SignInAsync()); // AutoCreate is off

        _engine.LdapOptions.AutoCreate = true;
        _engine.LdapOptions.AllowedDomains = ["example.com"];
        _engine.LdapOptions.DefaultRoles = ["Clerk"];
        Assert.Equal("ann@example.com", await SignInAsync());

        var session = (await _engine.Auth.LoadSessionAsync("ann@example.com"))!;
        Assert.Equal("Ann Lee", session.FullName);
        Assert.Equal(["All", "Clerk"], session.User.Roles.Order());
        Assert.Null(await _engine.Auth.LoginAsync("ann@example.com", Secret)); // the directory password is not stored here
    }

    [Fact]
    public async Task Other_domains_are_refused_without_asking_the_directory()
    {
        _engine.LdapOptions.AllowedDomains = ["example.com"];
        _engine.LdapDirectory.Add("uid=eve,dc=evil", "eve@evil.example", "evil-pass-1234");

        Assert.Null(await SignInAsync("eve@evil.example", "evil-pass-1234"));
        Assert.Equal(0, _engine.LdapDirectory.Searches);
    }

    [Fact]
    public async Task An_account_tied_to_one_directory_entry_cannot_be_taken_by_another()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);
        await SignInAsync();
        _engine.LdapDirectory.ChangeEmail(Dn, "old@example.com");
        _engine.LdapDirectory.Add("uid=other,dc=example,dc=com", "ann@example.com", "other-pass-12345");

        Assert.Null(await SignInAsync(password: "other-pass-12345"));
    }

    [Fact]
    public async Task A_directory_outage_is_a_failure_to_sign_in_not_a_way_in()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", []);
        _engine.LdapDirectory.Down = true;

        await Assert.ThrowsAsync<IOException>(() => SignInAsync()); // the endpoint turns this into an error, never a session
    }

    [Fact]
    public async Task It_does_nothing_when_switched_off()
    {
        _engine.LdapOptions.Enabled = false;

        Assert.Null(await SignInAsync());
        Assert.False(await _engine.Ldap.IsDirectoryUserAsync("ann@example.com"));
        Assert.Equal(0, _engine.LdapDirectory.Searches);
    }

    [Theory]
    [InlineData("", "dc=x", "(mail={0})", true, false, false, "needs Server")]
    [InlineData("ldap.example.com", "dc=x", "(mail=fixed)", true, false, false, "{0}")]
    [InlineData("ldap.example.com", "dc=x", "(mail={0})", false, false, false, "sends passwords")]
    [InlineData("ldap.example.com", "dc=x", "(mail={0})", true, false, true, "AllowedDomains")]
    public void Unsafe_or_incomplete_settings_are_reported(string server, string baseDn, string filter, bool tls, bool startTls, bool autoCreate, string expected)
    {
        var problem = LdapService.Problem(new LdapOptions { Enabled = true, Server = server, BaseDn = baseDn, UserFilter = filter, UseTls = tls, StartTls = startTls, AutoCreate = autoCreate });

        Assert.Contains(expected, problem);
    }

    [Fact]
    public void Sound_settings_and_a_local_test_server_are_accepted()
    {
        Assert.Null(LdapService.Problem(new LdapOptions { Enabled = true, Server = "ldap.example.com", BaseDn = "dc=x" }));
        Assert.Null(LdapService.Problem(new LdapOptions { Enabled = true, Server = "ldap.example.com", BaseDn = "dc=x", UseTls = false, StartTls = true }));
        Assert.Null(LdapService.Problem(new LdapOptions { Enabled = true, Server = "localhost", BaseDn = "dc=x", UseTls = false }));
        Assert.Null(LdapService.Problem(new LdapOptions { Enabled = false }));
    }
}
