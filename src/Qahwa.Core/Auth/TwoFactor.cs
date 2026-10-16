using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>Time-based one-time passwords (RFC 6238 with HMAC-SHA1, 30 seconds, as every authenticator app expects).</summary>
public static class Totp
{
    public const int PeriodSeconds = 30;
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret() => ToBase32(RandomNumberGenerator.GetBytes(20));

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var text = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
            text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    public static byte[]? FromBase32(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.Trim().TrimEnd('=').ToUpperInvariant())
        {
            if (c is ' ' or '-')
                continue;
            var value = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (value < 0)
                return null;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return bytes.ToArray();
    }

    public static long StepOf(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds() / PeriodSeconds;

    public static string Code(byte[] key, long step, int digits = 6)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulus = (int)Math.Pow(10, digits);
        return (binary % modulus).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The time step the code belongs to, or null when it is wrong. One step either side of now is accepted (clock drift), and a step
    /// at or before <paramref name="lastUsedStep"/> never is, so a code works once.
    /// </summary>
    public static long? Verify(string secretBase32, string? code, DateTime now, long lastUsedStep)
    {
        var key = FromBase32(secretBase32);
        if (key is null || key.Length == 0 || code is null || code.Length != 6 || !code.All(char.IsAsciiDigit))
            return null;

        var current = StepOf(now);
        long? matched = null;
        for (var step = current - 1; step <= current + 1; step++)
        {
            // Every candidate is compared, in constant time, so timing does not say which one was close.
            var same = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(key, step)), Encoding.ASCII.GetBytes(code));
            if (same && step > lastUsedStep)
                matched = step;
        }

        return matched;
    }

    public static string Uri(string issuer, string account, string secret) =>
        $"otpauth://totp/{System.Uri.EscapeDataString(issuer)}:{System.Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={System.Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={PeriodSeconds}";
}

/// <summary>Encrypts small secrets for storage (the real one uses ASP.NET Core Data Protection).</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    /// <returns>The text, or null when it was not protected by this system or has been tampered with.</returns>
    string? Unprotect(string protectedText);
}

public sealed record TotpRecord(string User, string Secret, bool Enabled, long LastStep, IReadOnlyList<string> RecoveryHashes, DateTime UserCreated);

public interface ITotpStore
{
    Task<TotpRecord?> GetAsync(string user, CancellationToken ct = default);

    /// <summary>Creates or replaces the record.</summary>
    Task SaveAsync(TotpRecord record, CancellationToken ct = default);

    Task DeleteAsync(string user, CancellationToken ct = default);

    /// <summary>Atomically moves "last used step" forward. False when the step was already used (or is older), so a code works once.</summary>
    Task<bool> TryAdvanceStepAsync(string user, long step, CancellationToken ct = default);

    /// <summary>Atomically removes one recovery code. False when it is not there (wrong, or already used).</summary>
    Task<bool> TryConsumeRecoveryAsync(string user, string hash, CancellationToken ct = default);
}

public sealed record TwoFactorSetup(string Secret, string Uri);

public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);

public sealed class InvalidCodeException()
    : QahwaException("That code is not right.", 401, "invalid_code");

/// <summary>
/// Two-factor sign-in with an authenticator app. After the password is right the person gets a short-lived challenge, and only a valid
/// code (or a one-time recovery code) turns it into a session. Wrong codes count against the same lockout as wrong passwords, a code
/// works once, and secrets and recovery codes are stored encrypted or hashed, never readable.
/// </summary>
public sealed class TwoFactorService(ITotpStore store, ISecretProtector protector, IAuthStore auth, IOptions<AuthOptions> options, TimeProvider clock)
{
    public const int RecoveryCodeCount = 10;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    /// <summary>The stored record, only while it belongs to the current account of that email (a deleted and re-made user starts clean).</summary>
    private async Task<TotpRecord?> RecordAsync(string user, CancellationToken ct)
    {
        var record = await store.GetAsync(user, ct);
        var credential = await auth.GetAsync(user, ct);
        return record is not null && credential is not null && record.UserCreated == credential.UserCreated ? record : null;
    }

    public async Task<bool> IsEnabledAsync(string user, CancellationToken ct = default) => (await RecordAsync(user, ct))?.Enabled == true;

    public async Task<TwoFactorStatus> StatusAsync(string user, CancellationToken ct = default)
    {
        var record = await RecordAsync(user, ct);
        return new TwoFactorStatus(record?.Enabled == true, record?.Enabled == true ? record.RecoveryHashes.Count : 0);
    }

    /// <summary>Makes a new secret (not active until a code from the app is confirmed). Replaces an earlier unconfirmed one.</summary>
    public async Task<TwoFactorSetup> BeginSetupAsync(string user, string issuer, CancellationToken ct = default)
    {
        if (await IsEnabledAsync(user, ct))
            throw new ConflictException("Two-factor sign-in is already on. Turn it off first to set it up again.");
        var credential = await auth.GetAsync(user, ct) ?? throw new UnauthenticatedException();

        var secret = Totp.NewSecret();
        await store.SaveAsync(new TotpRecord(user, protector.Protect(secret), false, 0, [], credential.UserCreated), ct);
        return new TwoFactorSetup(secret, Totp.Uri(issuer, user, secret));
    }

    /// <summary>Confirms the app shows the right codes, turns two-factor on and returns the recovery codes (shown once).</summary>
    public async Task<IReadOnlyList<string>> EnableAsync(string user, string? code, CancellationToken ct = default)
    {
        var record = await RecordAsync(user, ct);
        if (record is null || record.Enabled)
            throw new ConflictException("Start the setup first.");

        var secret = protector.Unprotect(record.Secret) ?? throw new ConflictException("Start the setup again.");
        var step = Totp.Verify(secret, code, Now(), 0);
        if (step is null)
            throw new DocumentValidationException([new FieldError("code", "That code is not right. Check the time on your phone and try the next code.")]);

        var plain = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        await store.SaveAsync(record with { Enabled = true, LastStep = step.Value, RecoveryHashes = plain.Select(HashRecovery).ToList() }, ct);
        return plain;
    }

    /// <summary>Turns two-factor off. Needs a current code or recovery code (the caller has checked the password).</summary>
    public async Task DisableAsync(string user, string? code, CancellationToken ct = default)
    {
        if (!await CheckCodeAsync(user, code, ct))
            throw new DocumentValidationException([new FieldError("code", "That code is not right.")]);
        await store.DeleteAsync(user, ct);
    }

    /// <summary>For a person who lost their phone and recovery codes: an administrator removes the second factor so they can sign in with the password and set it up again.</summary>
    public Task AdminResetAsync(string user, CancellationToken ct = default) => store.DeleteAsync(user, ct);

    // ---- the sign-in challenge ----

    public string CreateChallenge(string user) =>
        protector.Protect($"2fa|{user}|{(Now() + ChallengeLifetime).Ticks.ToString(CultureInfo.InvariantCulture)}");

    /// <returns>The user the challenge was made for, or null when it is forged, damaged or expired.</returns>
    public string? RedeemChallenge(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2000)
            return null;
        var plain = protector.Unprotect(token);
        var parts = plain?.Split('|');
        if (parts is not { Length: 3 } || parts[0] != "2fa" || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires))
            return null;
        return expires > Now().Ticks ? parts[1] : null;
    }

    /// <summary>
    /// The second step of signing in. A wrong code counts as a failed sign-in (so five wrong codes lock the account, as five wrong
    /// passwords do); a locked account is refused without looking at the code.
    /// </summary>
    public async Task<bool> VerifyLoginAsync(string user, string? code, CancellationToken ct = default)
    {
        var credential = await auth.GetAsync(user, ct);
        var now = Now();
        if (credential is null || credential.LockedUntil > now)
            return false;

        if (await CheckCodeAsync(user, code, ct))
            return true;

        var settings = options.Value;
        await auth.RecordFailureAsync(user, now, settings.MaxFailedAttempts, TimeSpan.FromMinutes(settings.LockoutMinutes), ct);
        return false;
    }

    private async Task<bool> CheckCodeAsync(string user, string? code, CancellationToken ct)
    {
        var record = await RecordAsync(user, ct);
        if (record is not { Enabled: true } || string.IsNullOrWhiteSpace(code) || code.Length > 40)
            return false;

        var trimmed = code.Trim();
        if (trimmed.Length == 6 && trimmed.All(char.IsAsciiDigit))
        {
            var secret = protector.Unprotect(record.Secret);
            return secret is not null && Totp.Verify(secret, trimmed, Now(), record.LastStep) is { } step && await store.TryAdvanceStepAsync(user, step, ct);
        }

        return await store.TryConsumeRecoveryAsync(user, HashRecovery(trimmed), ct);
    }

    private static string NewRecoveryCode()
    {
        var chars = Enumerable.Range(0, 10).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray();
        return new string(chars, 0, 5) + "-" + new string(chars, 5, 5);
    }

    /// <summary>Recovery codes are stored as SHA-256 of the code without dashes or spaces and in capitals, so how it is typed does not matter.</summary>
    public static string HashRecovery(string code)
    {
        var normalized = new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }
}
