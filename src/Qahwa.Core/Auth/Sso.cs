using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Qahwa.Core.Auth;

/// <summary>One identity provider people may sign in with (Entra ID, Google, Keycloak, Okta, any OpenID Connect server).</summary>
public sealed class SsoProvider
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The provider's address (its "issuer"), for example https://login.example.com/realms/main. Its settings are found at /.well-known/openid-configuration.</summary>
    public string Authority { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string Scopes { get; set; } = "openid email profile";

    /// <summary>Only people whose email is in one of these domains may sign in this way. Empty: any.</summary>
    public string[] AllowedDomains { get; set; } = [];

    /// <summary>Make an account for a person who has none yet (never without <see cref="AllowedDomains"/>). Off: only existing accounts can use it.</summary>
    public bool AutoCreate { get; set; }

    public string[] DefaultRoles { get; set; } = [];

    /// <summary>Believe the provider's email without its "email_verified" claim (for providers that do not send it, such as some Microsoft setups).</summary>
    public bool TrustEmail { get; set; }
}

public sealed class SsoOptions
{
    public Dictionary<string, SsoProvider> Providers { get; set; } = new(StringComparer.Ordinal);
}

public sealed record OidcMetadata(string Issuer, string AuthorizationEndpoint, string TokenEndpoint, string JwksUri);

/// <summary>Talking to the identity provider. The real one uses HTTPS with limits; tests use a stand-in.</summary>
public interface IOidcBackchannel
{
    Task<OidcMetadata> DiscoverAsync(string authority, CancellationToken ct = default);

    Task<JsonWebKeySet> KeysAsync(string jwksUri, bool refresh, CancellationToken ct = default);

    /// <returns>The "id_token" of the answer.</returns>
    Task<string?> ExchangeAsync(string tokenEndpoint, IReadOnlyDictionary<string, string> form, string? clientId, string? clientSecret, CancellationToken ct = default);
}

/// <summary>Which person at which provider is which user here. The provider's own stable id ("sub"), not the email, is what is trusted afterwards.</summary>
public interface IIdentityStore
{
    Task<string?> FindUserAsync(string issuer, string subject, CancellationToken ct = default);

    Task<IReadOnlyList<string>> SubjectsOfAsync(string issuer, string user, CancellationToken ct = default);

    /// <returns>False when that identity is already linked (to anyone).</returns>
    Task<bool> LinkAsync(string issuer, string subject, string user, DateTime now, CancellationToken ct = default);
}

public sealed record SsoStart(string Url, string State);

public sealed record SsoSignIn(string User, string Next, string Provider);

/// <summary>Why a single sign-on did not work. <see cref="Code"/> is safe to show; the reason is never explained to the browser.</summary>
public sealed class SsoException(string code, string detail) : Exception(detail)
{
    public string Code { get; } = code;
}

/// <summary>
/// Signing in with an OpenID Connect provider (authorization code flow with PKCE). Everything the provider says is checked: the
/// <c>state</c> and <c>nonce</c> (kept in an encrypted, short-lived value that only this browser holds), the token's signature against the
/// provider's published keys (only strong asymmetric algorithms, never "none" or a shared secret), its issuer, audience and lifetime.
/// A person is found by the provider's own id; the very first time, by a **verified** email of an existing account. The provider never
/// gives anyone more than an ordinary session for that account: no roles come from the token.
/// </summary>
public sealed class SsoService(
    Microsoft.Extensions.Options.IOptions<SsoOptions> options,
    IOidcBackchannel backchannel,
    IIdentityStore identities,
    AuthService auth,
    ISecretProtector protector,
    TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly string[] Algorithms = ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<(string Name, string DisplayName)> Providers() =>
        options.Value.Providers.Where(p => ValidName(p.Key) && p.Value.Authority.Length > 0 && p.Value.ClientId.Length > 0)
            .Select(p => (p.Key, string.IsNullOrWhiteSpace(p.Value.DisplayName) ? p.Key : p.Value.DisplayName)).OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToList();

    public static bool ValidName(string? name) => name is { Length: > 0 and <= 40 } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private SsoProvider Provider(string name) =>
        ValidName(name) && options.Value.Providers.TryGetValue(name, out var p) && p.Authority.Length > 0 && p.ClientId.Length > 0 ? p : throw new SsoException("unknown_provider", $"No provider '{name}'.");

    /// <summary>Only a path on this site, so a sign-in can never send someone to another address.</summary>
    public static string SafeNext(string? next) =>
        next is { Length: > 0 and <= 500 } && next[0] == '/' && !next.StartsWith("//", StringComparison.Ordinal) && !next.StartsWith("/\\", StringComparison.Ordinal) && !next.Any(char.IsControl) ? next : "/";

    private static string Random64(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public async Task<SsoStart> StartAsync(string providerName, string redirectUri, string? next, CancellationToken ct = default)
    {
        var provider = Provider(providerName);
        var metadata = await backchannel.DiscoverAsync(provider.Authority, ct);
        string state = Random64(24), nonce = Random64(24), verifier = Random64(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = new (string, string)[]
        {
            ("response_type", "code"), ("client_id", provider.ClientId), ("redirect_uri", redirectUri), ("scope", provider.Scopes),
            ("state", state), ("nonce", nonce), ("code_challenge", challenge), ("code_challenge_method", "S256"),
        };
        var url = metadata.AuthorizationEndpoint + (metadata.AuthorizationEndpoint.Contains('?') ? "&" : "?")
            + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Item1)}={Uri.EscapeDataString(q.Item2)}"));
        var payload = JsonSerializer.Serialize(new { p = providerName, s = state, n = nonce, v = verifier, x = (Now() + Lifetime).Ticks, next = SafeNext(next) });
        return new SsoStart(url, protector.Protect(payload));
    }

    private sealed record Saved(string P, string S, string N, string V, long X, string Next);

    public async Task<SsoSignIn> CompleteAsync(string providerName, string redirectUri, string? savedState, string? code, string? state, CancellationToken ct = default)
    {
        var provider = Provider(providerName);
        Saved? saved = null;
        try
        {
            var plain = string.IsNullOrEmpty(savedState) || savedState.Length > 4000 ? null : protector.Unprotect(savedState);
            saved = plain is null ? null : JsonSerializer.Deserialize<Saved>(plain, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // treated as no saved state below
        }

        if (saved is null || saved.P != providerName || saved.X <= Now().Ticks)
            throw new SsoException("expired", "The sign-in took too long or did not start here.");
        if (string.IsNullOrEmpty(state) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(saved.S)))
            throw new SsoException("bad_state", "The state did not match.");
        if (string.IsNullOrEmpty(code) || code.Length > 4000)
            throw new SsoException("no_code", "No code.");

        var metadata = await backchannel.DiscoverAsync(provider.Authority, ct);
        var idToken = await backchannel.ExchangeAsync(metadata.TokenEndpoint, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["code_verifier"] = saved.V,
        }, provider.ClientId, provider.ClientSecret, ct) ?? throw new SsoException("no_token", "The provider sent no id token.");

        var claims = await ValidateAsync(provider, metadata, idToken, saved.N, ct);
        var user = await ResolveAsync(provider, metadata.Issuer, claims, ct);
        return new SsoSignIn(user, saved.Next, providerName);
    }

    private sealed record Claims(string Subject, string? Email, bool EmailVerified, string? Name);

    private async Task<Claims> ValidateAsync(SsoProvider provider, OidcMetadata metadata, string idToken, string nonce, CancellationToken ct)
    {
        if (idToken.Length > 20_000)
            throw new SsoException("bad_token", "Too long.");
        var handler = new JsonWebTokenHandler();
        TokenValidationResult? result = null;
        for (var attempt = 0; attempt < 2 && result is not { IsValid: true }; attempt++)
        {
            // The second try asks the provider for its keys again, in case it rotated them since we last looked.
            var keys = await backchannel.KeysAsync(metadata.JwksUri, refresh: attempt == 1, ct);
            result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
            {
                ValidIssuer = metadata.Issuer, ValidAudience = provider.ClientId, IssuerSigningKeys = keys.GetSigningKeys(),
                ValidAlgorithms = Algorithms, RequireSignedTokens = true, RequireExpirationTime = true, ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(2), ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true,
            });
        }

        if (result is not { IsValid: true })
            throw new SsoException("bad_token", result?.Exception?.GetType().Name ?? "Not valid.");

        string? Text(string name) => result.Claims.TryGetValue(name, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        if (!string.Equals(Text("nonce"), nonce, StringComparison.Ordinal))
            throw new SsoException("bad_nonce", "The nonce did not match.");
        var subject = Text("sub");
        if (string.IsNullOrEmpty(subject) || subject.Length > 255)
            throw new SsoException("bad_token", "No subject.");
        var verified = string.Equals(Text("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
        return new Claims(subject, Text("email"), verified || provider.TrustEmail, Text("name"));
    }

    private Task<string> ResolveAsync(SsoProvider provider, string issuer, Claims claims, CancellationToken ct) =>
        new ExternalIdentityResolver(identities, auth, clock).ResolveAsync(new ExternalPolicy(provider.AllowedDomains, provider.AutoCreate, provider.DefaultRoles), issuer, claims.Subject, claims.Email, claims.EmailVerified, claims.Name, ct);
}

/// <summary>What may happen to a person who signs in through an outside system for the first time.</summary>
public sealed record ExternalPolicy(string[] AllowedDomains, bool AutoCreate, string[] DefaultRoles);

/// <summary>
/// Turns "this person, as this outside system knows them" into one of our users, the same way for every outside system (OpenID Connect,
/// SAML): the system's own id for the person is trusted first; the very first time, a verified email of an existing account (within the
/// allowed domains) is; an account that was switched off stays off; an account already tied to a different id is refused; unknown people
/// get an account only when allowed to, and then only the configured roles.
/// </summary>
public sealed class ExternalIdentityResolver(IIdentityStore identities, AuthService auth, TimeProvider clock)
{
    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }

    public async Task<string> ResolveAsync(ExternalPolicy provider, string issuer, string subject, string? claimedEmail, bool emailVerified, string? claimedName, CancellationToken ct = default)
    {
        // Someone already linked: trust the provider's own id, not the email (which can change or be given to someone else).
        if (await identities.FindUserAsync(issuer, subject, ct) is { } known)
            return await auth.LoadSessionAsync(known, ct) is null ? throw new SsoException("disabled", "The account is disabled.") : known;

        var email = AuthService.Normalize(claimedEmail);
        if (email is null || !EmailService.ValidAddress(email) || !emailVerified)
            throw new SsoException("email_not_verified", "The provider did not give a verified email.");
        var domain = email[(email.LastIndexOf('@') + 1)..];
        if (provider.AllowedDomains.Length > 0 && !provider.AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            throw new SsoException("domain_not_allowed", "That email domain is not allowed.");

        var state = await auth.AccountStateAsync(email, ct);
        if (state == AccountState.Disabled)
            throw new SsoException("disabled", "The account is disabled."); // single sign-on never switches an account back on

        if (state is AccountState.Active or AccountState.NoPassword)
        {
            // A first sign-in by an account that exists. If that account is already tied to a *different* person at this provider, refuse:
            // someone else who obtained the same email there must not be able to take over.
            if ((await identities.SubjectsOfAsync(issuer, email, ct)).Any(s => s != subject))
                throw new SsoException("already_linked", "That account is linked to another identity.");
            if (state == AccountState.NoPassword)
                await auth.SetPasswordAsync(email, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), ct); // a sign-in needs a credential record; nobody knows this password
        }
        else if (provider.AutoCreate && provider.AllowedDomains.Length > 0)
        {
            var name = string.IsNullOrWhiteSpace(claimedName) ? email[..email.LastIndexOf('@')] : claimedName.Trim();
            await auth.EnsureUserAsync(email, name.Length > 140 ? name[..140] : name, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), provider.DefaultRoles, ct);
        }
        else
        {
            throw new SsoException("no_account", "There is no account for that email.");
        }

        if (!await identities.LinkAsync(issuer, subject, email, Now(), ct) && await identities.FindUserAsync(issuer, subject, ct) != email)
            throw new SsoException("already_linked", "That identity is linked to another account.");
        return email;
    }
}
