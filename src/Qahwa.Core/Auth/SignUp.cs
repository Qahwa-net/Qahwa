using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>Settings for people making their own account (<c>Qahwa:Auth:SignUp</c>). Off unless <see cref="Enabled"/> is set.</summary>
public sealed class SignUpOptions
{
    public bool Enabled { get; set; }

    /// <summary>When not empty, only addresses at these domains (exact match, e.g. example.com) may sign up.</summary>
    public string[] AllowedDomains { get; set; } = [];

    /// <summary>The roles a new account gets (besides the automatic "All"). The Administrator role is never given this way.</summary>
    public string[] DefaultRoles { get; set; } = [];

    public int LinkHours { get; set; } = 24;
}

/// <summary>A verification message that has been prepared but not yet sent.</summary>
public sealed record SignUpMail(string To, string Subject, string Body, string Link);

public sealed class InvalidSignUpLinkException()
    : QahwaException("This link has expired or was already used. Start again.", 400, "invalid_signup_link");

public sealed class SignUpDisabledException()
    : QahwaException("Sign-up is not available.", 404, "not_found");

/// <summary>
/// Self sign-up with email verification. Nothing is stored until the address is proven: the link carries an encrypted token naming the
/// address, the name and an expiry, and the account is made (with the password the person chooses on that page) only when the link is
/// used. So an address nobody confirms never becomes an account, nobody can squat on someone else's address, and no password travels by
/// email. The answer to "sign up" is the same whether or not the address already has an account (an existing one is sent a note
/// saying so instead), and mailing is limited per address and per source.
/// </summary>
public sealed class SignUpService(
    AuthService auth,
    ISecretProtector protector,
    IEmailSender sender,
    HitCounter hits,
    IOptions<SignUpOptions> options,
    TimeProvider clock)
{
    public const int MaxNameLength = 140;
    public const int LinksPerHour = 3;
    public const int RequestsPerSourcePerHour = 10;

    public bool Enabled => options.Value.Enabled;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    /// <summary>The message to send, or null when none should be (an enabled account gets a different note; too many requests get nothing).</summary>
    /// <exception cref="SignUpDisabledException">Sign-up is off.</exception>
    /// <exception cref="DocumentValidationException">The address or name is not acceptable (this says nothing about existing accounts).</exception>
    public async Task<SignUpMail?> PrepareAsync(string? email, string? fullName, string baseUrl, string source, CancellationToken ct = default)
    {
        if (!Enabled)
            throw new SignUpDisabledException();

        var key = AuthService.Normalize(email);
        if (key is null || !EmailService.ValidAddress(key))
            throw new DocumentValidationException([new FieldError("email", "Enter a valid email address.")]);
        var name = CleanName(fullName) ?? throw new DocumentValidationException([new FieldError("full_name", "Enter your name (up to 140 characters).")]);
        var domain = key[(key.LastIndexOf('@') + 1)..];
        var allowed = options.Value.AllowedDomains;
        if (allowed.Length > 0 && !allowed.Any(d => string.Equals(d.Trim().TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase)))
            throw new DocumentValidationException([new FieldError("email", "Accounts can only be made for certain email domains.")]);

        if (!hits.TryHit($"signup-src:{source}", RequestsPerSourcePerHour, TimeSpan.FromHours(1)) || !hits.TryHit($"signup:{key}", LinksPerHour, TimeSpan.FromHours(1)))
            return null;

        var baseLink = baseUrl.TrimEnd('/');
        if (await auth.AccountStateAsync(key, ct) != AccountState.None)
        {
            return new SignUpMail(key, "You already have an account",
                $"Hello {name},\n\nSomeone (hopefully you) tried to sign up with this address, but it already has an account. Sign in here:\n\n{baseLink}/login\n\n" +
                $"If you forgot your password, you can reset it here:\n\n{baseLink}/forgot-password\n\nIf this was not you, you can ignore this message.\n", $"{baseLink}/login");
        }

        var expires = Now().AddHours(Math.Clamp(options.Value.LinkHours, 1, 168));
        var token = protector.Protect($"signup|{key}|{Convert.ToBase64String(Encoding.UTF8.GetBytes(name))}|{expires.Ticks.ToString(CultureInfo.InvariantCulture)}");
        var link = $"{baseLink}/sign-up/confirm?token={Uri.EscapeDataString(token)}";
        return new SignUpMail(key, "Confirm your email address",
            $"Hello {name},\n\nTo finish making your account, confirm your email address and choose a password by opening this link:\n\n{link}\n\n" +
            "If you did not ask for this, ignore this message: no account has been made.\n", link);
    }

    /// <summary>Sends without making the person who asked wait (and without the time it takes telling whether the account exists).</summary>
    public void SendInBackground(SignUpMail mail, Action<Exception> failed) =>
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

    /// <returns>The address and name the link is for, or null when it is forged, expired, or its address already has an account.</returns>
    public async Task<(string Email, string FullName)?> RedeemAsync(string? token, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrEmpty(token) || token.Length > 2000)
            return null;
        var parts = protector.Unprotect(token)?.Split('|');
        if (parts is not { Length: 4 } || parts[0] != "signup" || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var expires) || expires <= Now().Ticks)
            return null;

        string name;
        try
        {
            name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
        }
        catch (FormatException)
        {
            return null;
        }

        return await auth.AccountStateAsync(parts[1], ct) == AccountState.None ? (parts[1], name) : null;
    }

    /// <summary>Makes the account with the chosen password (checked against the password rules). The link works once: the account then exists.</summary>
    public async Task ConfirmAsync(string? token, string? password, CancellationToken ct = default)
    {
        var (email, name) = await RedeemAsync(token, ct) ?? throw new InvalidSignUpLinkException();
        var roles = options.Value.DefaultRoles.Where(r => !string.IsNullOrWhiteSpace(r) && r != UserContext.AdministratorRole).Select(r => r.Trim());
        try
        {
            await auth.CreateUserAsync(email, name, password ?? string.Empty, roles, ct);
        }
        catch (ConflictException)
        {
            throw new InvalidSignUpLinkException(); // a second click, or two at once
        }
    }

    /// <summary>
    /// The name trimmed and with runs of blanks made single, or null when empty, too long, or holding anything but letters (any script),
    /// digits and a few marks. The name goes into an email to an address that has not been proven yet, so it must not carry links or markup.
    /// </summary>
    public static string? CleanName(string? name)
    {
        if (name is null)
            return null;
        var clean = string.Join(' ', name.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return clean.Length is 0 or > MaxNameLength || !clean.All(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '\'' or '’' or ',' || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark) ? null : clean;
    }
}
