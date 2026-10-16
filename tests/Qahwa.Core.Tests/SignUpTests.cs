using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class SignUpTests
{
    private const string Base = "https://qahwa.example.com";
    private const string Good = "a-long-passphrase-9";

    private readonly TestEngine _engine = new();

    private static string TokenOf(SignUpMail mail) => Uri.UnescapeDataString(mail.Link[(mail.Link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

    private async Task<SignUpMail> AskAsync(string email = "new@example.com", string name = "New Person", string source = "1.2.3.4") =>
        (await _engine.SignUp.PrepareAsync(email, name, Base, source))!;

    [Fact]
    public async Task A_link_is_mailed_and_nothing_is_made_until_it_is_used()
    {
        var mail = await AskAsync("New@Example.com ");

        Assert.Equal("new@example.com", mail.To);
        Assert.StartsWith($"{Base}/sign-up/confirm?token=", mail.Link);
        Assert.Contains(mail.Link, mail.Body);
        Assert.Equal(AccountState.None, await _engine.Auth.AccountStateAsync("new@example.com"));

        await _engine.SignUp.ConfirmAsync(TokenOf(mail), Good);

        Assert.Equal(AccountState.Active, await _engine.Auth.AccountStateAsync("new@example.com"));
        var session = (await _engine.Auth.LoginAsync("new@example.com", Good))!;
        Assert.Equal("New Person", session.FullName);
        Assert.Equal(["All", "Clerk"], session.User.Roles.Order()); // Clerk comes from DefaultRoles
    }

    [Fact]
    public async Task The_link_works_once()
    {
        var token = TokenOf(await AskAsync());
        await _engine.SignUp.ConfirmAsync(token, Good);

        await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(token, "another-long-passphrase-7"));
        Assert.NotNull(await _engine.Auth.LoginAsync("new@example.com", Good)); // the first password still stands
        Assert.Null(await _engine.SignUp.RedeemAsync(token));
    }

    [Fact]
    public async Task Two_people_using_the_link_at_once_make_one_account()
    {
        var token = TokenOf(await AskAsync());

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            try
            {
                await _engine.SignUp.ConfirmAsync(token, Good);
                return true;
            }
            catch (Exception)
            {
                return false; // a lost race fails, but two never succeed
            }
        }));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task The_link_expires_and_forged_ones_are_refused()
    {
        var token = TokenOf(await AskAsync());

        foreach (var bad in new[] { "", "garbage", token + "x", token[..^3], new string('a', 3000) })
            await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(bad, Good));
        await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(null, Good));

        _engine.Clock.Advance(TimeSpan.FromHours(25));
        await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(token, Good));
        Assert.Equal(AccountState.None, await _engine.Auth.AccountStateAsync("new@example.com"));
    }

    [Fact]
    public async Task A_link_made_for_another_purpose_cannot_be_used_here()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Good, []);
        var reset = (await _engine.PasswordReset.PrepareAsync("ann@example.com", Base))!;
        var token = Uri.UnescapeDataString(reset.Link[(reset.Link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);

        await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(token, Good));
    }

    [Fact]
    public async Task The_password_must_pass_the_rules_and_a_weak_one_leaves_the_link_usable()
    {
        var token = TokenOf(await AskAsync());

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.SignUp.ConfirmAsync(token, "password"));
        Assert.Equal("password", error.Errors[0].Field);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.SignUp.ConfirmAsync(token, null));
        Assert.Equal(AccountState.None, await _engine.Auth.AccountStateAsync("new@example.com"));

        await _engine.SignUp.ConfirmAsync(token, Good);
    }

    [Fact]
    public async Task An_address_that_already_has_an_account_gets_a_note_and_the_account_is_never_touched()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Good, ["Manager"]);

        var mail = await AskAsync("ann@example.com", "Impostor");

        Assert.Equal("You already have an account", mail.Subject);
        Assert.DoesNotContain("token=", mail.Body); // no way to take the account over
        Assert.Equal("Ann Lee", (await _engine.Auth.LoadSessionAsync("ann@example.com"))!.FullName);
        Assert.NotNull(await _engine.Auth.LoginAsync("ann@example.com", Good));
    }

    [Fact]
    public async Task A_link_issued_before_the_account_appeared_is_dead_afterwards()
    {
        var token = TokenOf(await AskAsync());
        await _engine.Auth.EnsureUserAsync("new@example.com", "Made By Admin", Good, ["Manager"]);

        await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(token, "another-long-passphrase-7"));
        Assert.Equal("Made By Admin", (await _engine.Auth.LoadSessionAsync("new@example.com"))!.FullName);
    }

    [Fact]
    public async Task A_disabled_account_is_not_revived_by_signing_up_again()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Good, []);
        await _engine.Documents.UpdateAsync("User", "ann@example.com", TestEngine.Values(("enabled", false)), TestUsers.Admin);

        var mail = await AskAsync("ann@example.com");

        Assert.Equal("You already have an account", mail.Subject);
        Assert.Equal(AccountState.Disabled, await _engine.Auth.AccountStateAsync("ann@example.com"));
    }

    [Fact]
    public async Task Sign_up_is_off_unless_switched_on()
    {
        _engine.SignUpOptions.Enabled = false;

        await Assert.ThrowsAsync<SignUpDisabledException>(() => _engine.SignUp.PrepareAsync("a@example.com", "A", Base, "x"));
        _engine.SignUpOptions.Enabled = true;
        var token = TokenOf(await AskAsync());
        _engine.SignUpOptions.Enabled = false;
        await Assert.ThrowsAsync<InvalidSignUpLinkException>(() => _engine.SignUp.ConfirmAsync(token, Good)); // an old link does not work once it is off
    }

    [Fact]
    public async Task Only_allowed_domains_can_sign_up_and_lookalikes_do_not_pass()
    {
        _engine.SignUpOptions.AllowedDomains = ["example.com"];

        await AskAsync("ok@Example.com");
        foreach (var email in new[] { "x@evil.example", "x@example.com.evil.example", "x@sub.example.com", "x@notexample.com" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.SignUp.PrepareAsync(email, "X", Base, "s"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("a@b")]
    [InlineData("x\r\nBcc: victim@example.com@example.com")]
    [InlineData("a b@example.com")]
    public async Task Bad_addresses_are_refused(string email) =>
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.SignUp.PrepareAsync(email, "Name", Base, "s"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Visit https://evil.example/x now")]
    [InlineData("<b>bold</b>")]
    [InlineData("Line\r\nBreak")]
    [InlineData("Name\u0000")]
    public async Task Names_that_could_carry_links_or_markup_are_refused(string? name) =>
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.SignUp.PrepareAsync("a@example.com", name, Base, "s"));

    [Theory]
    [InlineData("  Ann   Lee ", "Ann Lee")]
    [InlineData("أمل البشا", "أمل البشا")]
    [InlineData("Seán O'Brien-Smith, Jr.", "Seán O'Brien-Smith, Jr.")]
    public void Ordinary_names_are_kept_in_any_script(string raw, string expected) => Assert.Equal(expected, SignUpService.CleanName(raw));

    [Fact]
    public async Task Too_long_a_name_is_refused()
    {
        Assert.Null(SignUpService.CleanName(new string('a', 141)));
        Assert.NotNull(SignUpService.CleanName(new string('a', 140)));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.SignUp.PrepareAsync("a@example.com", new string('a', 141), Base, "s"));
    }

    [Fact]
    public async Task Mailing_is_limited_per_address_and_per_source()
    {
        for (var i = 0; i < SignUpService.LinksPerHour; i++)
            Assert.NotNull(await _engine.SignUp.PrepareAsync("same@example.com", "Same", Base, "s1"));
        Assert.Null(await _engine.SignUp.PrepareAsync("same@example.com", "Same", Base, "s1")); // the fourth gets nothing

        for (var i = 0; i < SignUpService.RequestsPerSourcePerHour - SignUpService.LinksPerHour - 1; i++)
            Assert.NotNull(await _engine.SignUp.PrepareAsync($"p{i}@example.com", "P", Base, "s1"));
        Assert.Null(await _engine.SignUp.PrepareAsync("another@example.com", "P", Base, "s1")); // one source cannot flood many addresses
        Assert.NotNull(await _engine.SignUp.PrepareAsync("another@example.com", "P", Base, "s2"));

        _engine.Clock.Advance(TimeSpan.FromHours(1.1));
        Assert.NotNull(await _engine.SignUp.PrepareAsync("same@example.com", "Same", Base, "s1"));
    }

    [Fact]
    public async Task The_administrator_role_is_never_given_by_sign_up()
    {
        _engine.SignUpOptions.DefaultRoles = [UserContext.AdministratorRole, "Clerk", " "];

        await _engine.SignUp.ConfirmAsync(TokenOf(await AskAsync()), Good);

        var roles = (await _engine.Auth.LoadSessionAsync("new@example.com"))!.User.Roles;
        Assert.DoesNotContain(UserContext.AdministratorRole, roles);
        Assert.Contains("Clerk", roles);
    }
}
