using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class PasswordPolicyTests
{
    private static string? Problem(string? password, string email = "ann.smith@example.com", string? name = "Ann Smith") =>
        PasswordPolicy.Problem(password, email, name, 10, 128);

    [Theory]
    [InlineData("correct-horse-battery")]
    [InlineData("a-long-password-1")]
    [InlineData("Tr0ub4dor&3-xylophone")]
    [InlineData("مرحبا-بالقهوة-العربية-1")]
    public void Good_passwords_are_accepted(string password) => Assert.Null(Problem(password));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short1!")]
    [InlineData("aaaaaaaaaaaa")] // one character
    [InlineData("abababababab")] // two characters
    [InlineData("1234567890")] // a sequence
    [InlineData("abcdefghijkl")]
    [InlineData("9876543210")]
    [InlineData("Password1234")] // common word with digits
    [InlineData("passw0rd")] // too short and common
    [InlineData("Qwertyuiop!!")]
    [InlineData("ann.smith@example.com")] // the email itself
    [InlineData("AnnSmith2026")] // the person's own name
    [InlineData("annsmith!!!!")]
    public void Weak_passwords_are_refused_with_a_reason(string? password) => Assert.NotNull(Problem(password));

    [Fact]
    public void Too_long_is_refused_and_a_name_inside_a_long_passphrase_is_fine()
    {
        Assert.NotNull(PasswordPolicy.Problem(new string('x', 200) + "1a2b", "a@example.com", null, 10, 128));
        Assert.Null(Problem("the-quick-brown-fox-ann-jumps-over")); // a short name inside a long phrase
    }
}

public class PasswordResetTests
{
    private const string Email = "ann@example.com";
    private const string Old = "an-old-passphrase-7";
    private const string New = "a-brand-new-passphrase-9";

    private readonly TestEngine _engine = new();

    private async Task<string> LinkTokenAsync()
    {
        await _engine.Auth.EnsureUserAsync(Email, "Ann Lee", Old, ["Clerk"]);
        _engine.Clock.Advance(TimeSpan.FromMinutes(5));
        var mail = await _engine.PasswordReset.PrepareAsync(Email, "https://qahwa.example.com");
        Assert.NotNull(mail);
        return Uri.UnescapeDataString(mail.Link[(mail.Link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
    }

    [Fact]
    public async Task The_message_has_a_link_on_the_configured_site_and_says_to_ignore_it_if_unasked()
    {
        await _engine.Auth.EnsureUserAsync(Email, "Ann Lee", Old, []);

        var mail = (await _engine.PasswordReset.PrepareAsync(Email, "https://qahwa.example.com/"))!;

        Assert.Equal(Email, mail.To);
        Assert.StartsWith("https://qahwa.example.com/reset-password?token=", mail.Link);
        Assert.Contains(mail.Link, mail.Body);
        Assert.Contains("Hello Ann Lee", mail.Body);
        Assert.Contains("ignore", mail.Body);
    }

    [Fact]
    public async Task Unknown_disabled_and_malformed_addresses_give_nothing_to_send()
    {
        await _engine.Auth.EnsureUserAsync(Email, "Ann", Old, []);

        Assert.Null(await _engine.PasswordReset.PrepareAsync("nobody@example.com", "https://x.example"));
        Assert.Null(await _engine.PasswordReset.PrepareAsync("not an email", "https://x.example"));
        Assert.Null(await _engine.PasswordReset.PrepareAsync("a@b.com\r\nBcc: evil@example.com", "https://x.example"));
        Assert.Null(await _engine.PasswordReset.PrepareAsync(null, "https://x.example"));
        await _engine.Documents.UpdateAsync("User", Email, TestEngine.Values(("enabled", false)), TestUsers.Admin);
        Assert.Null(await _engine.PasswordReset.PrepareAsync(Email, "https://x.example"));
    }

    [Fact]
    public async Task Three_links_an_hour_per_address_so_nobody_can_flood_an_inbox()
    {
        await _engine.Auth.EnsureUserAsync(Email, "Ann", Old, []);

        for (var i = 0; i < PasswordResetService.LinksPerHour; i++)
            Assert.NotNull(await _engine.PasswordReset.PrepareAsync(Email, "https://x.example"));
        Assert.Null(await _engine.PasswordReset.PrepareAsync(Email, "https://x.example"));
        _engine.Clock.Advance(TimeSpan.FromMinutes(61));
        Assert.NotNull(await _engine.PasswordReset.PrepareAsync(Email, "https://x.example"));
    }

    [Fact]
    public async Task The_link_sets_a_new_password_once_and_ends_every_session()
    {
        var token = await LinkTokenAsync();
        var sessionId = await _engine.Sessions.StartAsync(Email, null, null);

        await _engine.PasswordReset.ResetAsync(token, New);

        Assert.NotNull(await _engine.Auth.VerifyPasswordAsync(Email, New));
        Assert.Null(await _engine.Auth.VerifyPasswordAsync(Email, Old));
        Assert.False(await _engine.Sessions.IsValidAsync(sessionId, Email));
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ResetAsync(token, "another-passphrase-55")); // used
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_and_the_link_still_works()
    {
        var token = await LinkTokenAsync();

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.PasswordReset.ResetAsync(token, "password1234"));
        Assert.Contains(error.Errors, e => e.Field == "new_password");
        Assert.NotNull(await _engine.Auth.VerifyPasswordAsync(Email, Old)); // nothing changed

        await _engine.PasswordReset.ResetAsync(token, New);
    }

    [Fact]
    public async Task The_link_expires_after_an_hour_and_is_dead_once_the_password_changes_another_way()
    {
        var token = await LinkTokenAsync();
        _engine.Clock.Advance(PasswordResetService.Lifetime + TimeSpan.FromMinutes(1));
        await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ResetAsync(token, New));

        var second = await _engine.PasswordReset.PrepareAsync(Email, "https://x.example");
        var token2 = Uri.UnescapeDataString(second!.Link[(second.Link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Auth.ChangePasswordAsync(Email, Old, New);
        await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ResetAsync(token2, "yet-another-passphrase-3"));
    }

    [Fact]
    public async Task Forged_and_foreign_links_never_work()
    {
        await _engine.Auth.EnsureUserAsync(Email, "Ann", Old, []);
        await _engine.Auth.EnsureUserAsync("bob@example.com", "Bob", Old, []);

        foreach (var token in new[] { null, "", "garbage", new string('a', 3000), "fake:" + Convert.ToBase64String("reset|ann@example.com|9999999999999999999|1"u8.ToArray()) + ":0000000000000000", _engine.TwoFactor.CreateChallenge(Email) })
            await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ResetAsync(token, New));
        Assert.NotNull(await _engine.Auth.VerifyPasswordAsync(Email, Old));
    }

    [Fact]
    public async Task A_reset_does_not_switch_off_two_factor()
    {
        var token = await LinkTokenAsync();
        var setup = await _engine.TwoFactor.BeginSetupAsync(Email, "Qahwa");
        await _engine.TwoFactor.EnableAsync(Email, Totp.Code(Totp.FromBase32(setup.Secret)!, Totp.StepOf(_engine.Clock.GetUtcNow().UtcDateTime)));

        await _engine.PasswordReset.ResetAsync(token, New);

        Assert.True(await _engine.TwoFactor.IsEnabledAsync(Email)); // the password alone is still not enough
    }

    [Fact]
    public async Task Sending_in_the_background_reports_failures_instead_of_throwing()
    {
        await _engine.Auth.EnsureUserAsync(Email, "Ann", Old, []);
        var mail = (await _engine.PasswordReset.PrepareAsync(Email, "https://x.example"))!;
        _engine.EmailSender.Fail(1);
        var failed = new TaskCompletionSource<Exception>();

        _engine.PasswordReset.SendInBackground(mail, failed.SetResult);

        Assert.NotNull(await failed.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
