using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class PasswordExpiryTests
{
    private const string Email = "ann@example.com";
    private const string Old = "an-old-passphrase-7";
    private const string New = "a-brand-new-passphrase-9";

    private readonly TestEngine _engine = new();

    private async Task SeedAsync(int maxAgeDays)
    {
        _engine.AuthOptions.PasswordMaxAgeDays = maxAgeDays;
        await _engine.Auth.EnsureUserAsync(Email, "Ann Lee", Old, ["Clerk"]);
    }

    [Fact]
    public async Task Passwords_do_not_expire_unless_asked_to()
    {
        await SeedAsync(0);
        _engine.Clock.Advance(TimeSpan.FromDays(3650));

        Assert.False(await _engine.Auth.PasswordExpiredAsync(Email));
    }

    [Fact]
    public async Task A_password_expires_after_the_chosen_number_of_days()
    {
        await SeedAsync(30);

        _engine.Clock.Advance(TimeSpan.FromDays(29));
        Assert.False(await _engine.Auth.PasswordExpiredAsync(Email));
        _engine.Clock.Advance(TimeSpan.FromDays(2));
        Assert.True(await _engine.Auth.PasswordExpiredAsync(Email));
    }

    [Fact]
    public async Task An_expired_password_is_replaced_with_a_token_and_a_new_different_password()
    {
        await SeedAsync(30);
        _engine.Clock.Advance(TimeSpan.FromDays(31));
        var token = await _engine.PasswordReset.CreateExpiredTokenAsync(Email);

        await _engine.PasswordReset.ChangeExpiredAsync(token, Old, New);

        Assert.NotNull(await _engine.Auth.VerifyPasswordAsync(Email, New));
        Assert.Null(await _engine.Auth.VerifyPasswordAsync(Email, Old));
        Assert.False(await _engine.Auth.PasswordExpiredAsync(Email)); // the clock starts again
        await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ChangeExpiredAsync(token, New, "yet-another-passphrase-3")); // the token died with the old password
    }

    [Fact]
    public async Task The_old_password_must_be_right_and_the_new_one_different_and_strong()
    {
        await SeedAsync(30);
        _engine.Clock.Advance(TimeSpan.FromDays(31));
        var token = await _engine.PasswordReset.CreateExpiredTokenAsync(Email);

        var wrongOld = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.PasswordReset.ChangeExpiredAsync(token, "not-my-password-1", New));
        Assert.Contains(wrongOld.Errors, e => e.Field == "old_password");
        var same = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.PasswordReset.ChangeExpiredAsync(token, Old, Old));
        Assert.Contains(same.Errors, e => e.Field == "new_password");
        var weak = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.PasswordReset.ChangeExpiredAsync(token, Old, "password1234"));
        Assert.Contains(weak.Errors, e => e.Field == "new_password");
        Assert.NotNull(await _engine.Auth.VerifyPasswordAsync(Email, Old)); // still the old password
    }

    [Fact]
    public async Task Guessing_the_old_password_through_the_token_locks_the_account_like_a_sign_in()
    {
        await SeedAsync(30);
        _engine.Clock.Advance(TimeSpan.FromDays(31));
        var token = await _engine.PasswordReset.CreateExpiredTokenAsync(Email);

        for (var i = 0; i < _engine.AuthOptions.MaxFailedAttempts; i++)
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.PasswordReset.ChangeExpiredAsync(token, $"guess-number-{i}-xx", New));

        Assert.Null(await _engine.Auth.VerifyPasswordAsync(Email, Old)); // locked, even with the right password
    }

    [Fact]
    public async Task Forged_stale_and_foreign_tokens_never_work()
    {
        await SeedAsync(30);
        _engine.Clock.Advance(TimeSpan.FromDays(31));
        var token = await _engine.PasswordReset.CreateExpiredTokenAsync(Email);

        foreach (var bad in new[] { null, "", "garbage", token + "x", new string('a', 3000), _engine.TwoFactor.CreateChallenge(Email) })
            await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ChangeExpiredAsync(bad, Old, New));
        _engine.Clock.Advance(TimeSpan.FromMinutes(11));
        await Assert.ThrowsAsync<InvalidResetLinkException>(() => _engine.PasswordReset.ChangeExpiredAsync(token, Old, New)); // ten minutes only
        Assert.Null(await _engine.PasswordReset.CreateExpiredTokenAsync("nobody@example.com"));
    }
}
