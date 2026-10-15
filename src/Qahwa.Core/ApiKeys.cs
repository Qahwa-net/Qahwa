using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

public sealed record ApiKeyInfo(
    string Id, string KeyId, string User, string? Description, bool ReadOnly, string? ExpiresOn, DateTime Created, DateTime? LastUsed, bool Expired);

/// <param name="Token">The whole key. It is shown once, when the key is made, and cannot be recovered afterwards.</param>
public sealed record ApiKeyCreated(ApiKeyInfo Info, string Token);

public sealed record ApiKeyRequest(string? Description = null, bool ReadOnly = false, string? ExpiresOn = null);

/// <summary>Who a valid key stands for.</summary>
public sealed record ApiKeyIdentity(UserContext User, bool ReadOnly);

/// <summary>
/// API keys let programs act as a person without their password: <c>Authorization: Bearer qk_&lt;id&gt;.&lt;secret&gt;</c>. A key has
/// exactly the permissions of the person it belongs to (never more), can be read-only, can expire, and stops working when
/// the person is disabled. Only a hash of the secret is stored, so a leaked database does not leak keys.
/// </summary>
public sealed class ApiKeyService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    AuthService auth,
    PermissionService permissions,
    HistoryRecorder history)
{
    public const string DocTypeName = "API Key";
    public const string Prefix = "qk_";
    public const int MaxPerUser = 10;
    public const int MaxDescription = 140;
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static DateOnly? ParseExpiry(string? text, DateOnly today)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Invalid("expiresOn", "Use a date like 2026-12-31.");
        if (date <= today)
            throw Invalid("expiresOn", "The expiry date must be in the future.");
        return date;
    }

    private ApiKeyInfo ToInfo(Dictionary<string, object?> row)
    {
        var expires = row["expires_on"] switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), _ => (DateOnly?)null };
        return new ApiKeyInfo(
            (string)row["name"]!, (string)row["key_id"]!, (string)row["user"]!, row["description"] as string, row["read_only"] is true,
            expires?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), (DateTime)row["creation"]!, row["last_used"] as DateTime?,
            expires is { } e && e <= DateOnly.FromDateTime(history.Now()));
    }

    /// <summary>Makes a key for the person asking, for themselves. The token is in the answer once and never again.</summary>
    public async Task<ApiKeyCreated> CreateAsync(ApiKeyRequest request, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length > MaxDescription || description.Any(char.IsControl))
            throw Invalid("description", $"Keep the description to {MaxDescription} characters of plain text.");
        var expires = ParseExpiry(request.ExpiresOn, DateOnly.FromDateTime(history.Now()));

        var mine = await repository.CountAsync(Meta, new ListQuery { Filters = [new("user", FilterOp.Eq, user.User)] }, ct);
        if (mine >= MaxPerUser)
            throw Invalid("description", $"You can have at most {MaxPerUser} API keys. Delete one you no longer use.");

        var keyId = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["key_id"] = keyId;
        row["user"] = user.User;
        row["description"] = description.Length == 0 ? null : description;
        row["secret_hash"] = Hash(secret);
        row["read_only"] = request.ReadOnly;
        row["expires_on"] = expires;
        await repository.InsertAsync(Meta, row, null, ct);
        return new ApiKeyCreated(ToInfo((await repository.GetAsync(Meta, (string)row["name"]!, ct))!), $"{Prefix}{keyId}.{secret}");
    }

    /// <summary>The person's keys, newest first (a System Manager can ask for everyone's).</summary>
    public async Task<IReadOnlyList<ApiKeyInfo>> ListAsync(IUserContext user, bool everyone = false, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        List<QueryFilter> filters = everyone && IsManager(user) ? [] : [new("user", FilterOp.Eq, user.User)];
        var rows = await repository.ListAsync(Meta, new ListQuery { Filters = filters, OrderBy = "creation", Descending = true, Limit = 500 }, ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <summary>Deletes a key: the person's own, or any for a System Manager. Programs using it stop working at once.</summary>
    public async Task RevokeAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        if (row is null || !(IsManager(user) || string.Equals(row["user"] as string, user.User, StringComparison.Ordinal)))
            throw new NotFoundException("API key not found.");
        await repository.DeleteAsync(Meta, id, null, ct);
    }

    /// <summary>
    /// Who a token stands for, or null when it is not a valid, enabled, unexpired key of an active user. The person's roles are
    /// read fresh, so taking a role away takes it from their keys too.
    /// </summary>
    public async Task<ApiKeyIdentity?> AuthenticateAsync(string? token, CancellationToken ct = default)
    {
        if (token is null || !token.StartsWith(Prefix, StringComparison.Ordinal) || token.Length > 200)
            return null;
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
            return null;
        var keyId = token[Prefix.Length..dot];
        var secret = token[(dot + 1)..];
        if (keyId.Length != 12 || secret.Length < 32 || keyId.Any(c => !char.IsAsciiHexDigitLower(c)))
            return null;

        var rows = await repository.ListAsync(Meta, new ListQuery { Filters = [new("key_id", FilterOp.Eq, keyId)], Limit = 1 }, ct);
        if (rows.Count == 0)
            return null;
        var row = rows[0];

        var expected = Encoding.ASCII.GetBytes((string)row["secret_hash"]!);
        var actual = Encoding.ASCII.GetBytes(Hash(secret));
        if (expected.Length != actual.Length || !CryptographicOperations.FixedTimeEquals(expected, actual))
            return null;
        if (row["expires_on"] switch { DateOnly d => d, DateTime dt => DateOnly.FromDateTime(dt), _ => (DateOnly?)null } is { } expiry
            && expiry <= DateOnly.FromDateTime(history.Now()))
            return null;

        var session = await auth.LoadSessionAsync((string)row["user"]!, ct);
        if (session is null)
            return null;

        // Remember when it was last used, without writing on every request.
        if (row["last_used"] is not DateTime last || history.Now() - last > TouchEvery)
        {
            try
            {
                await repository.UpdateAsync(Meta, (string)row["name"]!, new Dictionary<string, object?> { ["last_used"] = history.Now() }, null, null, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Bookkeeping only: never refuse a good key because it could not be recorded.
            }
        }

        return new ApiKeyIdentity(session.User, row["read_only"] is true);
    }
}
