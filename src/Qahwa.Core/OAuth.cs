using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

public sealed record OAuthScopeInfo(string Name, string Description);

public sealed record OAuthClientInfo(string Id, string ClientId, string Name, IReadOnlyList<string> RedirectUris, IReadOnlyList<string> Scopes, bool Public, bool Enabled, DateTime Created);

/// <param name="ClientSecret">Shown once, when a confidential client is made; null for a public client.</param>
public sealed record OAuthClientCreated(OAuthClientInfo Info, string? ClientSecret);

public sealed record OAuthClientRequest(string? Name, string? RedirectUris, IReadOnlyList<string>? Scopes, bool Public = false);

public sealed record OAuthAuthorizeRequest(string? ClientId, string? RedirectUri, string? Scope, string? State, string? CodeChallenge, string? CodeChallengeMethod);

public sealed record OAuthConsent(string ClientName, string RedirectHost, IReadOnlyList<OAuthScopeInfo> Scopes);

public sealed record OAuthUserInfo(
    [property: System.Text.Json.Serialization.JsonPropertyName("sub")] string Sub,
    [property: System.Text.Json.Serialization.JsonPropertyName("name")] string Name,
    [property: System.Text.Json.Serialization.JsonPropertyName("email")] string Email);

public sealed record OAuthTokenResponse(string AccessToken, string TokenType, int ExpiresIn, string? RefreshToken, string Scope);

public sealed record OAuthIdentity(UserContext User, IReadOnlySet<string> Scopes, string ClientId)
{
    public bool CanUseApi => Scopes.Contains(OAuthService.ScopeApi) || Scopes.Contains(OAuthService.ScopeWrite);

    public bool CanWrite => Scopes.Contains(OAuthService.ScopeWrite);
}

/// <summary>An OAuth failure in the words of RFC 6749 (<c>error</c> and a description), with the HTTP status to send.</summary>
public sealed class OAuthException(string error, string description, int status = 400) : Exception(description)
{
    public string Error { get; } = error;
    public int Status { get; } = status;
}

/// <summary>
/// Qahwa as an OAuth 2.0 provider (ADR-094): other programs and sites can ask a person for permission to read their name and email or to use
/// the API as them. Only the authorization-code flow with PKCE (S256 required, for every client), refresh tokens that rotate, token
/// revocation and a user-info endpoint. Tokens are random, only their hash is stored, and they can never do more than the person can.
/// </summary>
public sealed class OAuthService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    AuthService auth,
    PermissionService permissions,
    HistoryRecorder history,
    HitCounter hits)
{
    public const string ClientDocType = "OAuth Client";
    public const string TokenDocType = "OAuth Token";
    public const string ScopeProfile = "profile";
    public const string ScopeApi = "api";
    public const string ScopeWrite = "api:write";
    public const string AccessPrefix = "qo_";
    public const int MaxClients = 100;
    public const int MaxRedirects = 10;
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);

    public static readonly IReadOnlyList<OAuthScopeInfo> Scopes =
    [
        new(ScopeProfile, "See your name and email address."),
        new(ScopeApi, "Read your data through the API, with your own permissions."),
        new(ScopeWrite, "Create, change and delete your data through the API, with your own permissions."),
    ];

    private DocTypeMeta Clients => registry.Get(ClientDocType);

    private DocTypeMeta Tokens => registry.Get(TokenDocType);

    private DateTime Now() => history.Now();

    private static OAuthException Fail(string error, string description, int status = 400) => new(error, description, status);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NewSecret() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static bool SameHash(string expected, string actual)
    {
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    // ---- clients (System Manager) -----------------------------------------------------------------------------

    private static IReadOnlyList<string> Lines(string? text) =>
        (text ?? string.Empty).Split(['\n', '\r', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>A redirect address a client may register: https, http only on this computer (for programs on a laptop), or an app's own scheme (com.example.app:/done). Never a fragment or a password.</summary>
    public static string? RedirectProblem(string uri)
    {
        if (uri.Length > 500 || uri.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return "A redirect address must be short and have no spaces.";
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !string.IsNullOrEmpty(parsed.Fragment) || !string.IsNullOrEmpty(parsed.UserInfo))
            return $"'{uri}' is not an absolute address without a fragment or credentials.";
        if (parsed.Scheme == "https")
            return null;
        if (parsed.Scheme == "http")
            return parsed.IsLoopback ? null : $"'{uri}' must use https (http is allowed only for localhost).";
        // An app's private scheme, written the reverse-domain way (RFC 8252): never a scheme a browser would run.
        return parsed.Scheme.Contains('.') && parsed.Scheme is not ("javascript" or "data" or "file" or "vbscript" or "blob") ? null : $"'{uri}' uses a scheme that is not allowed.";
    }

    private static OAuthClientInfo ToInfo(Dictionary<string, object?> row) => new(
        (string)row["name"]!, (string)row["client_id"]!, (string)row["client_name"]!, Lines(row.GetValueOrDefault("redirect_uris") as string),
        Lines(row.GetValueOrDefault("scopes") as string), row.GetValueOrDefault("is_public") is true, row.GetValueOrDefault("enabled") is true, (DateTime)row["creation"]!);

    public async Task<OAuthClientCreated> CreateClientAsync(OAuthClientRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100 || name.Any(char.IsControl))
            throw Invalid("name", "Give the application a name of up to 100 characters.");
        var uris = Lines(request.RedirectUris);
        if (uris.Count is 0 or > MaxRedirects)
            throw Invalid("redirectUris", $"Give between 1 and {MaxRedirects} redirect addresses.");
        foreach (var uri in uris)
        {
            if (RedirectProblem(uri) is { } problem)
                throw Invalid("redirectUris", problem);
        }

        var scopes = (request.Scopes is { Count: > 0 } ? request.Scopes : [ScopeProfile]).Select(s => s.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (scopes.Any(s => Scopes.All(known => known.Name != s)))
            throw Invalid("scopes", $"Use only: {string.Join(", ", Scopes.Select(s => s.Name))}.");
        if (await repository.CountAsync(Clients, new ListQuery(), ct) >= MaxClients)
            throw Invalid("name", $"At most {MaxClients} applications can be registered.");

        var clientId = "qa_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();
        var secret = request.Public ? null : NewSecret();
        var row = HistoryRecorder.Standard(user.User, Now());
        row["client_id"] = clientId;
        row["client_name"] = name;
        row["redirect_uris"] = string.Join('\n', uris);
        row["scopes"] = string.Join(' ', scopes);
        row["is_public"] = request.Public;
        row["secret_hash"] = secret is null ? null : Hash(secret);
        row["enabled"] = true;
        await repository.InsertAsync(Clients, row, null, ct);
        return new OAuthClientCreated(ToInfo((await repository.GetAsync(Clients, (string)row["name"]!, ct))!), secret);
    }

    public async Task<IReadOnlyList<OAuthClientInfo>> ListClientsAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        return (await repository.ListAsync(Clients, new ListQuery { OrderBy = "creation", Descending = false, Limit = MaxClients }, ct)).Select(ToInfo).ToList();
    }

    /// <summary>Removes a client and every token it was given: programs using them stop working at once.</summary>
    public async Task DeleteClientAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var row = await repository.GetAsync(Clients, id, ct) ?? throw new NotFoundException("Application not found.");
        await RevokeWhereAsync([new("client_id", FilterOp.Eq, (string)row["client_id"]!)], ct);
        await repository.DeleteAsync(Clients, id, null, ct);
    }

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!IsManager(user))
            throw new PermissionDeniedException("Only a System Manager can manage OAuth applications.");
    }

    private async Task<Dictionary<string, object?>?> FindClientAsync(string? clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(clientId) || clientId.Length > 40)
            return null;
        var rows = await repository.ListAsync(Clients, new ListQuery { Filters = [new("client_id", FilterOp.Eq, clientId)], Limit = 1 }, ct);
        return rows.Count == 1 && rows[0].GetValueOrDefault("enabled") is true ? rows[0] : null;
    }

    // ---- the person's approval ---------------------------------------------------------------------------------

    private async Task<(Dictionary<string, object?> Client, List<string> Scopes, string RedirectUri)> CheckAsync(OAuthAuthorizeRequest request, CancellationToken ct)
    {
        // Until the client and the redirect address are known to be real, nothing may be sent to that address: these errors are shown to the person.
        var client = await FindClientAsync(request.ClientId, ct) ?? throw Fail("invalid_client", "This application is not known or has been switched off.");
        var redirect = request.RedirectUri ?? string.Empty;
        if (!Lines(client.GetValueOrDefault("redirect_uris") as string).Contains(redirect, StringComparer.Ordinal))
            throw Fail("invalid_request", "The redirect address does not match one registered for this application.");
        if (!string.Equals(request.CodeChallengeMethod, "S256", StringComparison.Ordinal) || request.CodeChallenge is not { Length: >= 43 and <= 128 } challenge
            || challenge.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~')))
            throw Fail("invalid_request", "This application must use PKCE with the S256 method.");
        var allowed = Lines(client.GetValueOrDefault("scopes") as string);
        var wanted = Lines(request.Scope).ToList();
        if (wanted.Count == 0)
            wanted = [ScopeProfile];
        if (wanted.Any(s => !allowed.Contains(s, StringComparer.Ordinal)))
            throw Fail("invalid_scope", "The application asked for permissions it is not registered for.");
        return (client, wanted, redirect);
    }

    /// <summary>What to ask the person: who is asking and what for. Throws an <see cref="OAuthException"/> for a request that must not be shown.</summary>
    public async Task<OAuthConsent> ConsentAsync(OAuthAuthorizeRequest request, CancellationToken ct = default)
    {
        var (client, scopes, redirect) = await CheckAsync(request, ct);
        return new OAuthConsent((string)client["client_name"]!, new Uri(redirect).Host, Scopes.Where(s => scopes.Contains(s.Name)).ToList());
    }

    /// <summary>The person answers. Returns the address to send them to: with a one-time code when they agreed, with <c>error=access_denied</c> when not.</summary>
    public async Task<string> DecideAsync(OAuthAuthorizeRequest request, bool approve, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var (client, scopes, redirect) = await CheckAsync(request, ct);
        var query = new List<string>();
        if (!approve)
        {
            query.Add("error=access_denied");
        }
        else
        {
            var code = "qc_" + NewSecret();
            var row = HistoryRecorder.Standard(user.User, Now());
            row["kind"] = "code";
            row["token_hash"] = Hash(code);
            row["client_id"] = client["client_id"];
            row["user"] = user.User;
            row["scope"] = string.Join(' ', scopes);
            row["expires_at"] = Now() + CodeLifetime;
            row["redirect_uri"] = redirect;
            row["code_challenge"] = request.CodeChallenge;
            row["family"] = (string)row["name"]!;
            await repository.InsertAsync(Tokens, row, null, ct);
            query.Add("code=" + Uri.EscapeDataString(code));
        }

        if (!string.IsNullOrEmpty(request.State))
            query.Add("state=" + Uri.EscapeDataString(request.State.Length > 500 ? request.State[..500] : request.State));
        return redirect + (redirect.Contains('?', StringComparison.Ordinal) ? "&" : "?") + string.Join('&', query);
    }

    // ---- the program asks for tokens -----------------------------------------------------------------------------

    private async Task<Dictionary<string, object?>?> FindTokenAsync(string? token, string kind, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 200)
            return null;
        var rows = await repository.ListAsync(Tokens, new ListQuery { Filters = [new("token_hash", FilterOp.Eq, Hash(token)), new("kind", FilterOp.Eq, kind)], Limit = 1 }, ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    private async Task RevokeWhereAsync(IReadOnlyList<QueryFilter> filters, CancellationToken ct)
    {
        while (true)
        {
            var rows = await repository.ListAsync(Tokens, new ListQuery { Filters = filters, Fields = ["name"], Limit = 200 }, ct);
            if (rows.Count == 0)
                return;
            foreach (var row in rows)
                await repository.DeleteAsync(Tokens, (string)row["name"]!, null, ct);
        }
    }

    private async Task<Dictionary<string, object?>> AuthenticateClientAsync(string? clientId, string? secret, CancellationToken ct)
    {
        if (!hits.TryHit($"oauth-token:{clientId}", 120, TimeSpan.FromMinutes(1)))
            throw Fail("slow_down", "Too many requests. Try again in a minute.", 429);
        var client = await FindClientAsync(clientId, ct) ?? throw Fail("invalid_client", "The client could not be authenticated.", 401);
        if (client.GetValueOrDefault("is_public") is not true)
        {
            var expected = client.GetValueOrDefault("secret_hash") as string;
            if (expected is null || string.IsNullOrEmpty(secret) || secret.Length > 200 || !SameHash(expected, Hash(secret)))
                throw Fail("invalid_client", "The client could not be authenticated.", 401);
        }

        return client;
    }

    private async Task<OAuthTokenResponse> IssueAsync(string clientId, string user, string scope, string family, CancellationToken ct)
    {
        var access = AccessPrefix + NewSecret();
        var refresh = "qr_" + NewSecret();
        foreach (var (kind, token, lifetime) in new[] { ("access", access, AccessLifetime), ("refresh", refresh, RefreshLifetime) })
        {
            var row = HistoryRecorder.Standard(user, Now());
            row["kind"] = kind;
            row["token_hash"] = Hash(token);
            row["client_id"] = clientId;
            row["user"] = user;
            row["scope"] = scope;
            row["expires_at"] = Now() + lifetime;
            row["family"] = family;
            await repository.InsertAsync(Tokens, row, null, ct);
        }

        return new OAuthTokenResponse(access, "Bearer", (int)AccessLifetime.TotalSeconds, refresh, scope);
    }

    /// <summary>The token endpoint: trades a code (with its PKCE verifier) or a refresh token for new tokens.</summary>
    public async Task<OAuthTokenResponse> TokenAsync(
        string? grantType, string? clientId, string? clientSecret, string? code, string? redirectUri, string? codeVerifier, string? refreshToken, CancellationToken ct = default)
    {
        var client = await AuthenticateClientAsync(clientId, clientSecret, ct);
        var id = (string)client["client_id"]!;
        if (grantType == "authorization_code")
        {
            var row = await FindTokenAsync(code, "code", ct);
            if (row is null || !string.Equals(row["client_id"] as string, id, StringComparison.Ordinal))
                throw Fail("invalid_grant", "The code is not valid.");
            var family = (string)row["family"]!;
            if (row.GetValueOrDefault("used") is true)
            {
                await RevokeWhereAsync([new("family", FilterOp.Eq, family)], ct); // a code used twice: whatever it produced is withdrawn
                throw Fail("invalid_grant", "The code has already been used.");
            }

            await repository.UpdateAsync(Tokens, (string)row["name"]!, new Dictionary<string, object?> { ["used"] = true, ["modified"] = Now() }, null, null, ct);
            if (row["expires_at"] is not DateTime expires || expires <= Now())
                throw Fail("invalid_grant", "The code has expired.");
            if (!string.Equals(row["redirect_uri"] as string, redirectUri, StringComparison.Ordinal))
                throw Fail("invalid_grant", "The redirect address does not match the one used to ask for the code.");
            if (string.IsNullOrEmpty(codeVerifier) || codeVerifier.Length is < 43 or > 128
                || !SameHash(Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))), (string)row["code_challenge"]!))
                throw Fail("invalid_grant", "The PKCE verifier does not match.");
            if (await auth.LoadSessionAsync((string)row["user"]!, ct) is null)
                throw Fail("invalid_grant", "The person can no longer sign in.");
            return await IssueAsync(id, (string)row["user"]!, (string)row["scope"]!, family, ct);
        }

        if (grantType == "refresh_token")
        {
            var row = await FindTokenAsync(refreshToken, "refresh", ct);
            if (row is null || !string.Equals(row["client_id"] as string, id, StringComparison.Ordinal))
                throw Fail("invalid_grant", "The refresh token is not valid.");
            var family = (string)row["family"]!;
            if (row.GetValueOrDefault("used") is true)
            {
                await RevokeWhereAsync([new("family", FilterOp.Eq, family)], ct); // a rotated token used again: it has leaked, withdraw the whole family
                throw Fail("invalid_grant", "The refresh token has already been used.");
            }

            if (row["expires_at"] is not DateTime expires || expires <= Now())
                throw Fail("invalid_grant", "The refresh token has expired.");
            if (await auth.LoadSessionAsync((string)row["user"]!, ct) is null)
                throw Fail("invalid_grant", "The person can no longer sign in.");
            await repository.UpdateAsync(Tokens, (string)row["name"]!, new Dictionary<string, object?> { ["used"] = true, ["modified"] = Now() }, null, null, ct);
            return await IssueAsync(id, (string)row["user"]!, (string)row["scope"]!, family, ct);
        }

        throw Fail("unsupported_grant_type", "Use authorization_code or refresh_token.");
    }

    /// <summary>RFC 7009: withdraws a token and everything issued from the same approval. Unknown tokens are ignored (the answer never says).</summary>
    public async Task RevokeAsync(string? clientId, string? clientSecret, string? token, CancellationToken ct = default)
    {
        var client = await AuthenticateClientAsync(clientId, clientSecret, ct);
        if (string.IsNullOrEmpty(token) || token.Length > 200)
            return;
        var rows = await repository.ListAsync(Tokens, new ListQuery { Filters = [new("token_hash", FilterOp.Eq, Hash(token))], Limit = 1 }, ct);
        if (rows.Count == 1 && string.Equals(rows[0]["client_id"] as string, (string)client["client_id"]!, StringComparison.Ordinal))
            await RevokeWhereAsync([new("family", FilterOp.Eq, (string)rows[0]["family"]!)], ct);
    }

    // ---- using a token ---------------------------------------------------------------------------------------------

    /// <summary>Who an access token stands for, or null when it is unknown, expired, withdrawn, or its client or person is gone.</summary>
    public async Task<OAuthIdentity?> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (token is null || !token.StartsWith(AccessPrefix, StringComparison.Ordinal))
            return null;
        var row = await FindTokenAsync(token, "access", ct);
        if (row is null || row["expires_at"] is not DateTime expires || expires <= Now())
            return null;
        if (await FindClientAsync(row["client_id"] as string, ct) is null)
            return null;
        var session = await auth.LoadSessionAsync((string)row["user"]!, ct);
        return session is null ? null : new OAuthIdentity(session.User, Lines(row["scope"] as string).ToHashSet(StringComparer.Ordinal), (string)row["client_id"]!);
    }

    // ---- what a person has allowed ----------------------------------------------------------------------------------

    public sealed record Connected(string ClientId, string Name, string Scope, DateTime Since);

    /// <summary>The applications this person has approved and that still hold a token.</summary>
    public async Task<IReadOnlyList<Connected>> ConnectedAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Tokens, new ListQuery { Filters = [new("user", FilterOp.Eq, user.User), new("kind", FilterOp.Eq, "refresh")], OrderBy = "creation", Descending = false, Limit = 500 }, ct);
        var list = new List<Connected>();
        foreach (var group in rows.Where(r => r.GetValueOrDefault("used") is not true && r["expires_at"] is DateTime e && e > Now()).GroupBy(r => (string)r["client_id"]!))
        {
            if (await FindClientAsync(group.Key, ct) is not { } client)
                continue;
            var first = group.OrderBy(r => (DateTime)r["creation"]!).First();
            list.Add(new Connected(group.Key, (string)client["client_name"]!, (string)first["scope"]!, (DateTime)first["creation"]!));
        }

        return list;
    }

    /// <summary>The person takes their approval back: every token the application holds for them stops working.</summary>
    public async Task DisconnectAsync(string clientId, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        await RevokeWhereAsync([new("user", FilterOp.Eq, user.User), new("client_id", FilterOp.Eq, clientId)], ct);
    }

    /// <summary>Removes tokens and codes that have expired (the scheduler calls this).</summary>
    public async Task<int> PurgeAsync(CancellationToken ct = default)
    {
        var removed = 0;
        while (true)
        {
            var old = await repository.ListAsync(Tokens, new ListQuery { Filters = [new("expires_at", FilterOp.Lt, Now() - TimeSpan.FromDays(1))], Fields = ["name"], Limit = 200 }, ct);
            if (old.Count == 0)
                return removed;
            foreach (var row in old)
            {
                await repository.DeleteAsync(Tokens, (string)row["name"]!, null, ct);
                removed++;
            }
        }
    }

    /// <summary>The person for the user-info endpoint: name and email, nothing else.</summary>
    public async Task<OAuthUserInfo> UserInfoAsync(OAuthIdentity identity, CancellationToken ct = default)
    {
        if (!identity.Scopes.Contains(ScopeProfile))
            throw Fail("insufficient_scope", "The token does not have the profile permission.", 403);
        var session = await auth.LoadSessionAsync(identity.User.User, ct) ?? throw Fail("invalid_token", "The person can no longer sign in.", 401);
        return new OAuthUserInfo(identity.User.User, session.FullName, identity.User.User);
    }
}
