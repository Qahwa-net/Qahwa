using System.Text.RegularExpressions;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

/// <summary>Two-factor sign-in with a code sent by email.</summary>
public class EmailCodeTests
{
    private const string User = "ann@example.com";

    private readonly TestEngine _engine = new();

    private async Task SeedAsync() => await _engine.Auth.EnsureUserAsync(User, "Ann", TestUsers.StrongPassword, ["Clerk"]);

    /// <summary>The code in the last email sent.</summary>
    private string LastCode() => Regex.Match(_engine.EmailSender.Sent[^1].Body, @"\b(\d{6})\b").Groups[1].Value;

    [Fact]
    public async Task The_option_is_off_until_turned_on_and_a_code_by_email_confirms_both_ways()
    {
        await SeedAsync();
        Assert.False(await _engine.EmailCodes.IsEnabledAsync(User));

        var token = await _engine.EmailCodes.SendAsync(User, "enable");
        Assert.Equal(User, _engine.EmailSender.Sent[^1].To);
        Assert.Matches("^\\d{6}$", LastCode());
        Assert.Equal(User, await _engine.EmailCodes.VerifyAsync(token, LastCode(), "enable"));
        await _engine.EmailCodes.SetEnabledAsync(User, true);
        Assert.True(await _engine.EmailCodes.IsEnabledAsync(User));

        await _engine.EmailCodes.SetEnabledAsync(User, false);
        Assert.False(await _engine.EmailCodes.IsEnabledAsync(User));
    }

    [Fact]
    public async Task A_code_works_once_for_ten_minutes_and_only_for_what_it_was_asked_for()
    {
        await SeedAsync();
        var token = await _engine.EmailCodes.SendAsync(User, "login");
        var code = LastCode();

        Assert.Null(await _engine.EmailCodes.VerifyAsync(token, code, "enable")); // a login code does not turn anything on
        Assert.Equal(User, await _engine.EmailCodes.VerifyAsync(token, code, "login"));
        Assert.Null(await _engine.EmailCodes.VerifyAsync(token, code, "login")); // used

        var late = await _engine.EmailCodes.SendAsync(User, "login");
        var lateCode = LastCode();
        _engine.Clock.Advance(EmailCodeService.Lifetime + TimeSpan.FromSeconds(1));
        Assert.Null(await _engine.EmailCodes.VerifyAsync(late, lateCode, "login")); // expired
    }

    [Fact]
    public async Task The_code_is_not_in_the_challenge_and_a_challenge_cannot_be_forged_or_swapped()
    {
        await SeedAsync();
        await _engine.Auth.EnsureUserAsync("bob@example.com", "Bob", TestUsers.StrongPassword, ["Clerk"]);
        var token = await _engine.EmailCodes.SendAsync(User, "login");
        var code = LastCode();

        Assert.DoesNotContain(code, token);
        Assert.Null(await _engine.EmailCodes.VerifyAsync(token + "x", code, "login"));
        Assert.Null(await _engine.EmailCodes.VerifyAsync(null, code, "login"));
        Assert.Null(await _engine.EmailCodes.VerifyAsync("2fe|login|bob@example.com|99999999999999999|n|h", code, "login")); // not made by the server

        // A code from one person's email does not open another person's challenge.
        var bob = await _engine.EmailCodes.SendAsync("bob@example.com", "login");
        Assert.Null(await _engine.EmailCodes.VerifyAsync(bob, code, "login"));
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_even_for_the_right_code()
    {
        await SeedAsync();
        var token = await _engine.EmailCodes.SendAsync(User, "login");
        var code = LastCode();

        for (var i = 0; i < 5; i++)
            Assert.Null(await _engine.EmailCodes.VerifyAsync(token, i == 0 ? "12345" : "000000", "login"));

        Assert.Null(await _engine.EmailCodes.VerifyAsync(token, code, "login")); // locked, the code is not even looked at
        _engine.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(User, await _engine.EmailCodes.VerifyAsync(await _engine.EmailCodes.SendAsync(User, "login"), LastCode(), "login"));
    }

    [Fact]
    public async Task Sending_is_limited_and_needs_an_address()
    {
        await SeedAsync();
        for (var i = 0; i < EmailCodeService.MaxSends; i++)
            await _engine.EmailCodes.SendAsync(User, "login");
        await Assert.ThrowsAsync<CodeRateLimitedException>(() => _engine.EmailCodes.SendAsync(User, "login"));
        _engine.Clock.Advance(EmailCodeService.SendWindow + TimeSpan.FromSeconds(1));
        await _engine.EmailCodes.SendAsync(User, "login");

        await Assert.ThrowsAsync<ConflictException>(() => _engine.EmailCodes.SendAsync("not-an-address", "login"));
    }
}
