using System.Text;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class TotpTests
{
    private static readonly byte[] Rfc = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void The_codes_match_the_RFC_6238_test_vectors(long seconds, string expected) =>
        Assert.Equal(expected, Totp.Code(Rfc, seconds / 30, 8));

    [Fact]
    public void Base32_round_trips_and_rejects_junk()
    {
        var secret = Totp.NewSecret();
        Assert.Equal(32, secret.Length);
        Assert.Equal(secret, Totp.ToBase32(Totp.FromBase32(secret)!));
        Assert.Equal(Rfc, Totp.FromBase32(Totp.ToBase32(Rfc)));
        Assert.Equal(Rfc, Totp.FromBase32(Totp.ToBase32(Rfc).ToLowerInvariant() + "  "));
        Assert.Null(Totp.FromBase32("not*base32!"));
    }

    [Fact]
    public void A_code_is_accepted_one_step_either_side_but_not_further_and_never_twice()
    {
        var secret = Totp.ToBase32(Rfc);
        var now = new DateTime(2026, 3, 1, 12, 0, 10, DateTimeKind.Utc);
        var step = Totp.StepOf(now);
        string At(long s) => Totp.Code(Rfc, s);

        Assert.Equal(step, Totp.Verify(secret, At(step), now, 0));
        Assert.Equal(step - 1, Totp.Verify(secret, At(step - 1), now, 0));
        Assert.Equal(step + 1, Totp.Verify(secret, At(step + 1), now, 0));
        Assert.Null(Totp.Verify(secret, At(step + 2), now, 0));
        Assert.Null(Totp.Verify(secret, At(step - 2), now, 0));
        Assert.Null(Totp.Verify(secret, At(step), now, step)); // already used
        Assert.Null(Totp.Verify(secret, At(step - 1), now, step)); // older than the last one used
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    [InlineData("12 456")]
    [InlineData("１２３４５６")] // full-width digits
    public void Codes_that_are_not_six_ascii_digits_are_refused(string code) =>
        Assert.Null(Totp.Verify(Totp.ToBase32(Rfc), code, DateTime.UtcNow, 0));

    [Fact]
    public void The_setup_address_names_the_app_and_the_account_safely()
    {
        var uri = Totp.Uri("Qahwa", "a&b@example.com", "ABCDEFGH");

        Assert.StartsWith("otpauth://totp/Qahwa:a%26b%40example.com?secret=ABCDEFGH&issuer=Qahwa", uri);
    }
}

public class TwoFactorTests
{
    private const string Password = "a-long-password-1";
    private const string User = "ann@example.com";

    private readonly TestEngine _engine = new();

    private async Task SeedAsync()
    {
        await _engine.Auth.EnsureUserAsync(User, "Ann", Password, ["Clerk"]);
        _engine.Clock.Advance(TimeSpan.FromHours(1));
    }

    private string CodeNow(string secret, int offsetSteps = 0) =>
        Totp.Code(Totp.FromBase32(secret)!, Totp.StepOf(_engine.Clock.GetUtcNow().UtcDateTime) + offsetSteps);

    private async Task<(string Secret, IReadOnlyList<string> Recovery)> TurnOnAsync()
    {
        await SeedAsync();
        var setup = await _engine.TwoFactor.BeginSetupAsync(User, "Qahwa");
        var recovery = await _engine.TwoFactor.EnableAsync(User, CodeNow(setup.Secret));
        _engine.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 2)); // the setup code is behind us
        return (setup.Secret, recovery);
    }

    [Fact]
    public async Task Setup_needs_a_right_code_before_it_turns_on_and_hands_out_ten_recovery_codes_once()
    {
        await SeedAsync();
        var setup = await _engine.TwoFactor.BeginSetupAsync(User, "Qahwa");
        Assert.False(await _engine.TwoFactor.IsEnabledAsync(User)); // not until confirmed

        var wrong = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.TwoFactor.EnableAsync(User, "000000"));
        Assert.Contains(wrong.Errors, e => e.Field == "code");
        Assert.False(await _engine.TwoFactor.IsEnabledAsync(User));

        var recovery = await _engine.TwoFactor.EnableAsync(User, CodeNow(setup.Secret));

        Assert.Equal(TwoFactorService.RecoveryCodeCount, recovery.Count);
        Assert.Equal(recovery.Count, recovery.Distinct().Count());
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", recovery[0]);
        Assert.Equal(new TwoFactorStatus(true, 10), await _engine.TwoFactor.StatusAsync(User));
        await Assert.ThrowsAsync<ConflictException>(() => _engine.TwoFactor.BeginSetupAsync(User, "Qahwa")); // already on
    }

    [Fact]
    public async Task The_secret_and_recovery_codes_are_never_stored_readable()
    {
        var (secret, recovery) = await TurnOnAsync();

        var stored = (await _engine.TotpStore.GetAsync(User))!;

        Assert.DoesNotContain(secret, stored.Secret);
        Assert.All(recovery, code => Assert.DoesNotContain(stored.RecoveryHashes, h => h.Contains(code.Replace("-", ""), StringComparison.OrdinalIgnoreCase)));
        Assert.All(stored.RecoveryHashes, h => Assert.Equal(64, h.Length));
    }

    [Fact]
    public async Task A_code_signs_in_once_and_the_next_one_works_after_the_clock_moves_on()
    {
        var (secret, _) = await TurnOnAsync();
        var code = CodeNow(secret);

        Assert.True(await _engine.TwoFactor.VerifyLoginAsync(User, code));
        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, code)); // replay
        _engine.Clock.Advance(TimeSpan.FromSeconds(Totp.PeriodSeconds * 3));
        Assert.True(await _engine.TwoFactor.VerifyLoginAsync(User, CodeNow(secret)));
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_account_even_for_the_right_code()
    {
        var (secret, _) = await TurnOnAsync();

        for (var i = 0; i < _engine.AuthOptions.MaxFailedAttempts; i++)
            Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, "000000"));

        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, CodeNow(secret))); // locked
        Assert.Null(await _engine.Auth.VerifyPasswordAsync(User, Password)); // and the password step too
        _engine.Clock.Advance(TimeSpan.FromMinutes(_engine.AuthOptions.LockoutMinutes + 1));
        Assert.True(await _engine.TwoFactor.VerifyLoginAsync(User, CodeNow(secret)));
    }

    [Fact]
    public async Task A_right_password_does_not_wipe_the_wrong_code_count()
    {
        var (secret, _) = await TurnOnAsync();
        for (var i = 0; i < _engine.AuthOptions.MaxFailedAttempts - 1; i++)
            await _engine.TwoFactor.VerifyLoginAsync(User, "000000");

        Assert.Equal(User, await _engine.Auth.VerifyPasswordAsync(User, Password)); // the password step again
        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, "000000")); // the fifth wrong code

        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, CodeNow(secret))); // locked: guessing in rounds does not work
    }

    [Fact]
    public async Task A_recovery_code_works_once_and_however_it_is_typed()
    {
        var (_, recovery) = await TurnOnAsync();
        var typed = recovery[0].ToLowerInvariant().Replace("-", " ");

        Assert.True(await _engine.TwoFactor.VerifyLoginAsync(User, typed));
        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, recovery[0]));
        Assert.Equal(9, (await _engine.TwoFactor.StatusAsync(User)).RecoveryCodesLeft);
        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, "AAAAA-AAAAA"));
    }

    [Fact]
    public async Task Turning_it_off_needs_a_valid_code_and_an_administrator_can_reset_it()
    {
        var (secret, _) = await TurnOnAsync();

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.TwoFactor.DisableAsync(User, "000000"));
        Assert.True(await _engine.TwoFactor.IsEnabledAsync(User));
        await _engine.TwoFactor.DisableAsync(User, CodeNow(secret));
        Assert.False(await _engine.TwoFactor.IsEnabledAsync(User));

        var again = await _engine.TwoFactor.BeginSetupAsync(User, "Qahwa");
        await _engine.TwoFactor.EnableAsync(User, CodeNow(again.Secret));
        await _engine.TwoFactor.AdminResetAsync(User);
        Assert.False(await _engine.TwoFactor.IsEnabledAsync(User));
    }

    [Fact]
    public async Task Without_two_factor_every_code_is_refused()
    {
        await SeedAsync();

        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, "123456"));
        Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, "AAAAA-AAAAA"));
    }

    [Fact]
    public async Task The_challenge_is_for_one_person_expires_and_cannot_be_forged()
    {
        await SeedAsync();
        var token = _engine.TwoFactor.CreateChallenge(User);

        Assert.Equal(User, _engine.TwoFactor.RedeemChallenge(token));
        Assert.Null(_engine.TwoFactor.RedeemChallenge(token + "x"));
        Assert.Null(_engine.TwoFactor.RedeemChallenge("2fa|admin@example.com|9999999999999999999"));
        Assert.Null(_engine.TwoFactor.RedeemChallenge(null));
        Assert.Null(_engine.TwoFactor.RedeemChallenge(new string('a', 5000)));
        _engine.Clock.Advance(TwoFactorService.ChallengeLifetime + TimeSpan.FromSeconds(1));
        Assert.Null(_engine.TwoFactor.RedeemChallenge(token));
    }

    [Fact]
    public async Task Hostile_codes_are_just_wrong_codes()
    {
        await TurnOnAsync();

        foreach (var code in new[] { "' OR 1=1 --", "<script>", new string('9', 500), "\0\0\0\0\0\0", "      " })
            Assert.False(await _engine.TwoFactor.VerifyLoginAsync(User, code));
    }
}
