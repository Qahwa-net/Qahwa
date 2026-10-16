using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

public sealed record SessionInfo(UserContext User, string FullName, long PasswordVersion);

public sealed class InvalidCredentialsException()
    : QahwaException("Invalid email or password.", 401, "invalid_credentials");

/// <summary>
/// Login, password and session lookup for users stored as <c>User</c> documents. Passwords are hashed with the
/// ASP.NET Core Identity PBKDF2 hasher and kept in <see cref="IAuthStore"/>, never in a DocType.
/// </summary>
public sealed class AuthService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    IAuthStore store,
    IPasswordHasher<string> hasher,
    IOptions<AuthOptions> options,
    TimeProvider clock)
{
    public const string UserDocType = "User";
    public const string RoleDocType = "Role";
    public const string HasRoleDocType = "Has Role";
    public const int MaxPasswordLength = 128;

    private static readonly UserContext SystemContext = new("System", [UserContext.AdministratorRole]);

    // Verified against when the account does not exist, so response time does not reveal which emails are registered.
    private readonly Lazy<string> _dummyHash = new(() => hasher.HashPassword("-", "timing-equalisation-password"));

    /// <returns>The session, or null for any failure (unknown, disabled, locked or wrong password).</returns>
    public async Task<SessionInfo?> LoginAsync(string? email, string? password, CancellationToken ct = default)
    {
        var key = await VerifyPasswordAsync(email, password, ct);
        return key is null ? null : await CompleteLoginAsync(key, ct);
    }

    /// <summary>
    /// The first step of signing in: checks the password with the lockout rules. Wrong passwords count; a right one does not clear the
    /// counter yet, so a second step (two-factor) cannot be guessed at by logging in again and again.
    /// </summary>
    /// <returns>The normalised email, or null for any failure.</returns>
    public async Task<string?> VerifyPasswordAsync(string? email, string? password, CancellationToken ct = default)
    {
        var key = Normalize(email);
        if (key is null || string.IsNullOrEmpty(password) || password.Length > MaxPasswordLength)
            return null;

        var settings = options.Value;
        var now = Now();
        var user = await GetUserAsync(key, ct);
        var credential = await store.GetAsync(key, ct);

        if (!IsActive(user, credential) || credential!.LockedUntil > now)
        {
            hasher.VerifyHashedPassword("-", _dummyHash.Value, password);
            return null;
        }

        switch (hasher.VerifyHashedPassword(key, credential.PasswordHash, password))
        {
            case PasswordVerificationResult.Failed:
                await store.RecordFailureAsync(
                    key, now, settings.MaxFailedAttempts, TimeSpan.FromMinutes(settings.LockoutMinutes), ct);
                return null;
            case PasswordVerificationResult.SuccessRehashNeeded:
                await store.RehashAsync(key, credential.PasswordHash, hasher.HashPassword(key, password), ct);
                break;
        }

        return key;
    }

    /// <summary>The last step of signing in: clears the failure counter and loads the session (null if the account stopped being usable).</summary>
    public async Task<SessionInfo?> CompleteLoginAsync(string key, CancellationToken ct = default)
    {
        await store.RecordSuccessAsync(key, Now(), ct);
        return await LoadSessionAsync(key, ct);
    }

    /// <summary>True when the password is right. Does not touch the lockout counters (callers are already signed in and rate limited).</summary>
    public async Task<bool> CheckPasswordAsync(string email, string? password, CancellationToken ct = default)
    {
        var key = Normalize(email);
        var credential = key is null ? null : await store.GetAsync(key, ct);
        return credential is not null && !string.IsNullOrEmpty(password) && password.Length <= MaxPasswordLength
            && hasher.VerifyHashedPassword(key!, credential.PasswordHash, password) != PasswordVerificationResult.Failed;
    }

    /// <summary>Loads the current identity, or null when the user is missing, disabled or has no valid password.</summary>
    public async Task<SessionInfo?> LoadSessionAsync(string email, CancellationToken ct = default)
    {
        var key = Normalize(email);
        if (key is null)
            return null;

        var user = await GetUserAsync(key, ct);
        var credential = await store.GetAsync(key, ct);
        if (!IsActive(user, credential))
            return null;

        var roles = await repository.ListAsync(
            registry.Get(HasRoleDocType),
            new ListQuery
            {
                Filters =
                [
                    new QueryFilter("user", FilterOp.Eq, key),
                    // Role grants made to an earlier account with the same email do not carry over.
                    new QueryFilter("creation", FilterOp.Ge, user!["creation"]),
                ],
                Fields = ["role"],
                OrderBy = "role",
                Descending = false,
                Limit = 1000,
            },
            ct);

        var names = roles.Select(r => (string)r["role"]!).Distinct(StringComparer.Ordinal).ToList();
        return new SessionInfo(new UserContext(key, names), (string)user["full_name"]!, credential!.PasswordChanged.Ticks);
    }

    public async Task SetPasswordAsync(string email, string newPassword, CancellationToken ct = default)
    {
        var key = Normalize(email) ?? throw new DocumentValidationException([new FieldError("email", "A valid email is required.")]);
        var user = await GetUserAsync(key, ct) ?? throw new NotFoundException($"User '{key}' was not found.");
        ValidatePassword(key, newPassword, "new_password", user.GetValueOrDefault("full_name") as string);
        await store.SetPasswordAsync(key, hasher.HashPassword(key, newPassword), (DateTime)user["creation"]!, Now(), ct);
    }

    public async Task ChangePasswordAsync(string email, string oldPassword, string newPassword, CancellationToken ct = default)
    {
        var key = Normalize(email) ?? throw new UnauthenticatedException();
        var credential = await store.GetAsync(key, ct) ?? throw new UnauthenticatedException();

        if (string.IsNullOrEmpty(oldPassword) || oldPassword.Length > MaxPasswordLength
            || hasher.VerifyHashedPassword(key, credential.PasswordHash, oldPassword) == PasswordVerificationResult.Failed)
        {
            throw new DocumentValidationException([new FieldError("old_password", "The current password is incorrect.")]);
        }

        await SetPasswordAsync(key, newPassword, ct);
    }

    /// <summary>Creates (or re-enables) a user, sets the password and grants the roles. Used to bootstrap the first admin.</summary>
    public async Task EnsureUserAsync(
        string email,
        string fullName,
        string password,
        IEnumerable<string> roles,
        CancellationToken ct = default)
    {
        var key = Normalize(email) ?? throw new DocumentValidationException([new FieldError("email", "A valid email is required.")]);
        ValidatePassword(key, password, "password", fullName);

        var user = await GetUserAsync(key, ct);
        if (user is null)
        {
            await documents.InsertAsync(
                UserDocType,
                new Dictionary<string, object?> { ["email"] = key, ["full_name"] = fullName, ["enabled"] = true },
                SystemContext,
                ct);
        }
        else if (user["enabled"] is not true)
        {
            await documents.UpdateAsync(
                UserDocType, key, new Dictionary<string, object?> { ["enabled"] = true }, SystemContext, ct);
        }

        await SetPasswordAsync(key, password, ct);

        var created = (DateTime)(await GetUserAsync(key, ct))!["creation"]!;
        foreach (var role in roles.Distinct(StringComparer.Ordinal))
        {
            if (!await repository.ExistsAsync(registry.Get(RoleDocType), role, ct))
            {
                await documents.InsertAsync(
                    RoleDocType, new Dictionary<string, object?> { ["role_name"] = role }, SystemContext, ct);
            }

            var existing = await repository.CountAsync(
                registry.Get(HasRoleDocType),
                new ListQuery
                {
                    Filters =
                    [
                        new QueryFilter("user", FilterOp.Eq, key),
                        new QueryFilter("role", FilterOp.Eq, role),
                        new QueryFilter("creation", FilterOp.Ge, created),
                    ],
                },
                ct);
            if (existing == 0)
            {
                await documents.InsertAsync(
                    HasRoleDocType,
                    new Dictionary<string, object?> { ["user"] = key, ["role"] = role },
                    SystemContext,
                    ct);
            }
        }
    }

    public static string? Normalize(string? email)
    {
        var key = email?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(key) || key.Length > FieldTypes.ShortTextLength ? null : key;
    }

    private void ValidatePassword(string email, string? password, string field, string? fullName)
    {
        var problem = PasswordPolicy.Problem(password, email, fullName, options.Value.MinPasswordLength, MaxPasswordLength);
        if (problem is not null)
            throw new DocumentValidationException([new FieldError(field, problem)]);
    }

    private Task<Dictionary<string, object?>?> GetUserAsync(string key, CancellationToken ct) =>
        repository.GetAsync(registry.Get(UserDocType), key, ct);

    private static bool IsActive(Dictionary<string, object?>? user, AuthCredential? credential) =>
        user is not null
        && credential is not null
        && user.GetValueOrDefault("enabled") is true
        && user.GetValueOrDefault("creation") is DateTime created
        && created == credential.UserCreated;

    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
