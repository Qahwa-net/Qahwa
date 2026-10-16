using System.Globalization;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>What a password must be: long enough, not a well-known password, not made of the person's own name or a trivial pattern.</summary>
public static class PasswordPolicy
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "password", "passw0rd", "p@ssword", "qwerty", "qwertyuiop", "qwertyui", "asdfghjkl", "zxcvbnm", "letmein", "welcome", "welcome1",
        "admin", "administrator", "root", "login", "master", "dragon", "monkey", "football", "baseball", "iloveyou", "sunshine", "princess",
        "superman", "batman", "trustno", "shadow", "abc", "abcdef", "abcdefgh", "abcdefghij", "changeme", "default", "secret", "letmein",
        "starwars", "whatever", "freedom", "hello", "hello123", "computer", "internet", "service", "google", "samsung", "iphone",
        "qazwsx", "zaq", "1q2w3e", "1qaz2wsx", "q1w2e3r4", "passpass", "pass", "test", "testing", "test123", "user", "guest", "demo",
        "qahwa", "coffee", "ahlan", "marhaba", "riyadh", "saudi", "ksa", "mdd",
    };

    /// <returns>What is wrong with the password, or null when it is acceptable.</returns>
    public static string? Problem(string? password, string email, string? fullName, int minLength, int maxLength)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
            return $"Password must be at least {minLength} characters long.";
        if (password.Length > maxLength)
            return $"Password must be at most {maxLength} characters long.";
        if (string.Equals(password, email, StringComparison.OrdinalIgnoreCase))
            return "Password cannot be your email address.";

        var plain = new string(password.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (plain.Length == 0 || plain.Distinct().Count() < 4)
            return "Password is too simple: use more different characters.";
        if (Sequential(plain))
            return "Password is a simple sequence such as 123456789 or abcdefghij.";
        if (Common.Contains(plain) || Common.Contains(plain.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9')))
            return "That password is too common. Choose something harder to guess.";

        foreach (var part in NameParts(email, fullName))
            if (plain.Contains(part, StringComparison.Ordinal) && part.Length * 2 >= plain.Length)
                return "Password is too close to your name or email.";
        return null;
    }

    private static IEnumerable<string> NameParts(string email, string? fullName)
    {
        var local = email.Split('@')[0];
        foreach (var raw in new[] { local, fullName ?? string.Empty }.SelectMany(s => s.Split(['.', '_', '-', ' ', '+'], StringSplitOptions.RemoveEmptyEntries).Append(s)))
        {
            var part = new string(raw.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
            if (part.Length >= 4)
                yield return part;
        }
    }

    private static bool Sequential(string text)
    {
        if (text.Length < 6)
            return false;
        if (text.All(char.IsAsciiDigit))
        {
            // Digits wrap around: 1234567890 and 9876543210 are sequences too.
            var d = (text[1] - text[0] + 10) % 10;
            return (d == 1 || d == 9) && Enumerable.Range(1, text.Length - 1).All(i => (text[i] - text[i - 1] + 10) % 10 == d);
        }

        var step = text[1] - text[0];
        return Math.Abs(step) <= 1 && Enumerable.Range(1, text.Length - 1).All(i => text[i] - text[i - 1] == step);
    }
}

/// <summary>A reset message that has been prepared but not yet sent.</summary>
public sealed record ResetMail(string To, string Subject, string Body, string Link);

/// <summary>
/// "Forgot password" by email. The link carries a signed, encrypted token naming the person, an expiry (one hour) and the time their
/// password last changed, so using it (or changing the password any other way) makes every earlier link useless; nothing is stored,
/// and the token is never written to a queue or document that administrators can read. The answer to "forgot" never says whether the
/// address has an account, and a person (or a script) cannot mail-bomb someone: three links an hour per address.
/// </summary>
public sealed class PasswordResetService(
    AuthService auth,
    IAuthStore store,
    ISecretProtector protector,
    SessionService sessions,
    IEmailSender sender,
    HitCounter hits,
    TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);
    public const int LinksPerHour = 3;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    /// <summary>The message to send, or null when there is nothing to send (no such usable account, or too many requests). The caller answers the same either way.</summary>
    public async Task<ResetMail?> PrepareAsync(string? email, string baseUrl, CancellationToken ct = default)
    {
        var key = AuthService.Normalize(email);
        if (key is null || !EmailService.ValidAddress(key) || !hits.TryHit($"reset:{key}", LinksPerHour, TimeSpan.FromHours(1)))
            return null;

        var session = await auth.LoadSessionAsync(key, ct);
        var credential = await store.GetAsync(key, ct);
        if (session is null || credential is null)
            return null;

        var token = protector.Protect($"reset|{key}|{(Now() + Lifetime).Ticks.ToString(CultureInfo.InvariantCulture)}|{credential.PasswordChanged.Ticks.ToString(CultureInfo.InvariantCulture)}");
        var link = $"{baseUrl.TrimEnd('/')}/reset-password?token={Uri.EscapeDataString(token)}";
        var body = $"Hello {session.FullName},\n\nSomeone asked to reset the password of your account. To choose a new one, open this link within an hour:\n\n{link}\n\n" +
                   "If you did not ask for this, ignore this message: your password has not changed.\n";
        return new ResetMail(key, "Reset your password", body, link);
    }

    /// <summary>Sends without making the person who asked wait (and without the time it takes telling whether the account exists).</summary>
    public void SendInBackground(ResetMail mail, Action<Exception> failed) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await sender.SendAsync(new EmailMessage(mail.To, mail.Subject, mail.Body));
            }
            catch (Exception ex)
            {
                failed(ex);
            }
        });

    // ---- an expired password ----

    /// <summary>
    /// Given instead of a session when the password is right but too old. It lasts ten minutes and is tied to the password it was made for,
    /// so choosing a new one (or any other change) ends it.
    /// </summary>
    public async Task<string?> CreateExpiredTokenAsync(string user, CancellationToken ct = default)
    {
        var credential = await store.GetAsync(user, ct);
        return credential is null ? null : protector.Protect($"expired|{user}|{(Now() + TimeSpan.FromMinutes(10)).Ticks.ToString(CultureInfo.InvariantCulture)}|{credential.PasswordChanged.Ticks.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Sets a new password in place of an expired one. The old password is asked for again, and the new one must be different.</summary>
    public async Task ChangeExpiredAsync(string? token, string? oldPassword, string? newPassword, CancellationToken ct = default)
    {
        var parts = string.IsNullOrEmpty(token) || token.Length > 2000 ? null : protector.Unprotect(token)?.Split('|');
        if (parts is not { Length: 4 } || parts[0] != "expired"
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var changed)
            || expires <= Now().Ticks)
            throw new InvalidResetLinkException();

        var credential = await store.GetAsync(parts[1], ct);
        if (credential is null || credential.PasswordChanged.Ticks != changed)
            throw new InvalidResetLinkException();

        if (await auth.VerifyPasswordAsync(parts[1], oldPassword, ct) is null) // counts wrong passwords like a sign-in does
            throw new DocumentValidationException([new FieldError("old_password", "The current password is not right.")]);
        if (string.Equals(oldPassword, newPassword, StringComparison.Ordinal))
            throw new DocumentValidationException([new FieldError("new_password", "Choose a password you have not been using.")]);

        await auth.SetPasswordAsync(parts[1], newPassword ?? string.Empty, ct);
        await sessions.RevokeAllForAsync(parts[1], ct);
    }

    /// <returns>The person the link is for, or null when it is forged, expired or already used.</returns>
    public async Task<string?> RedeemAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 2000)
            return null;
        var parts = protector.Unprotect(token)?.Split('|');
        if (parts is not { Length: 4 } || parts[0] != "reset"
            || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var changed))
            return null;
        if (expires <= Now().Ticks)
            return null;

        var credential = await store.GetAsync(parts[1], ct);
        return credential is not null && credential.PasswordChanged.Ticks == changed && await auth.LoadSessionAsync(parts[1], ct) is not null ? parts[1] : null;
    }

    /// <summary>Sets the new password (checked against the password rules), ends every session of the person, and makes the link useless.</summary>
    public async Task ResetAsync(string? token, string? newPassword, CancellationToken ct = default)
    {
        var user = await RedeemAsync(token, ct) ?? throw new InvalidResetLinkException();
        await auth.SetPasswordAsync(user, newPassword ?? string.Empty, ct);
        await sessions.RevokeAllForAsync(user, ct);
    }
}

public sealed class InvalidResetLinkException()
    : QahwaException("This link has expired or was already used. Ask for a new one.", 400, "invalid_reset_link");
