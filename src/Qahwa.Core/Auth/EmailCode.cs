using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>Said with status 429 when too many codes were asked for.</summary>
public sealed class CodeRateLimitedException()
    : QahwaException("Too many codes were sent. Wait a few minutes and try again.", 429, "rate_limited");

/// <summary>
/// Two-factor sign-in with a code sent by email, for people who have no authenticator app. After the password is right a six-digit code
/// is emailed and the person gets a short-lived challenge; only that code turns it into a session. The code is not stored anywhere: the
/// challenge (encrypted and signed) carries only a keyed hash of it, so a leaked database holds nothing to read. A code works once, for
/// ten minutes, wrong codes count against the same lockout as wrong passwords, and sending is limited per person.
/// The same mechanism confirms turning the option on and off. An authenticator app, when the person has one, takes precedence.
/// </summary>
public sealed class EmailCodeService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    IEmailSender sender,
    ISecretProtector protector,
    IAuthStore auth,
    IOptions<AuthOptions> options,
    TimeProvider clock)
{
    public const string FieldName = "email_two_factor";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public const int MaxSends = 5;
    public static readonly TimeSpan SendWindow = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, DateTime> _used = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<DateTime>> _sends = new(StringComparer.Ordinal);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private DocTypeMeta Users => registry.Get("User");

    public async Task<bool> IsEnabledAsync(string user, CancellationToken ct = default) =>
        (await repository.GetAsync(Users, user, ct))?.GetValueOrDefault(FieldName) is true;

    public async Task SetEnabledAsync(string user, bool enabled, CancellationToken ct = default) =>
        await repository.UpdateAsync(Users, user, new Dictionary<string, object?> { [FieldName] = enabled }, null, null, ct);

    private static string Hash(string code, string nonce, string user, string purpose) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{code}|{nonce}|{user}|{purpose}")));

    /// <summary>Emails a fresh code and returns the challenge to answer with it. <paramref name="purpose"/> is "login", "enable" or "disable".</summary>
    public async Task<string> SendAsync(string user, string purpose, CancellationToken ct = default)
    {
        if (!EmailService.ValidAddress(user))
            throw new ConflictException("This account has no email address to send a code to.");
        var now = Now();
        var recent = _sends.GetOrAdd(user, _ => []);
        lock (recent)
        {
            recent.RemoveAll(t => now - t > SendWindow);
            if (recent.Count >= MaxSends)
                throw new CodeRateLimitedException();
            recent.Add(now);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var token = protector.Protect($"2fe|{purpose}|{user}|{(now + Lifetime).Ticks.ToString(CultureInfo.InvariantCulture)}|{nonce}|{Hash(code, nonce, user, purpose)}");

        var what = purpose switch { "enable" => "turn on email codes", "disable" => "turn off email codes", _ => "sign in" };
        await sender.SendAsync(new EmailMessage(user, "Your Qahwa code",
            $"Your code to {what} is {code}.\n\nIt works once, for 10 minutes. If you did not ask for it, someone knows your password: change it now and do not share this code."), ct);
        return token;
    }

    /// <summary>
    /// The person the challenge was made for when the code is right, otherwise null. A code works once; a wrong one counts as a failed sign-in
    /// (so five wrong codes lock the account, as five wrong passwords do) and a locked account is refused without looking at the code.
    /// </summary>
    public async Task<string?> VerifyAsync(string? token, string? code, string purpose, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2000 || protector.Unprotect(token) is not { } plain)
            return null;
        var parts = plain.Split('|');
        if (parts is not { Length: 6 } || parts[0] != "2fe" || parts[1] != purpose
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) || expires <= Now().Ticks)
            return null;
        var user = parts[2];

        var credential = await auth.GetAsync(user, ct);
        var now = Now();
        if (credential is null || credential.LockedUntil > now)
            return null;

        var trimmed = (code ?? string.Empty).Trim();
        var right = trimmed.Length == 6 && trimmed.All(char.IsAsciiDigit)
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(trimmed, parts[4], user, purpose)), Encoding.ASCII.GetBytes(parts[5]));
        if (right)
        {
            foreach (var old in _used.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList())
                _used.TryRemove(old, out _);
            if (!_used.TryAdd(parts[4], new DateTime(expires, DateTimeKind.Utc)))
                return null; // the same code, used before
            return user;
        }

        var settings = options.Value;
        await auth.RecordFailureAsync(user, now, settings.MaxFailedAttempts, TimeSpan.FromMinutes(settings.LockoutMinutes), ct);
        return null;
    }
}
