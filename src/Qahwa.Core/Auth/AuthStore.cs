namespace Qahwa.Core.Auth;

public sealed class AuthOptions
{
    public int MinPasswordLength { get; set; } = 10;

    /// <summary>Failed logins before the account is locked.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    public int LockoutMinutes { get; set; } = 15;

    /// <summary>PBKDF2 iterations for new password hashes (OWASP guidance for HMAC-SHA512 is 210,000).</summary>
    public int HashIterations { get; set; } = 210_000;

    public int LoginPermitsPerMinute { get; set; } = 10;

    public int SessionHours { get; set; } = 8;

    /// <summary>
    /// How long a user's enabled flag, roles and password version are cached per user.
    /// A role change or disabled account takes at most this long to apply to open sessions. 0 disables the cache.
    /// </summary>
    public int SessionCacheSeconds { get; set; } = 15;
}

public sealed record AuthCredential(
    string PasswordHash,
    int FailedAttempts,
    DateTime? LockedUntil,
    DateTime PasswordChanged,
    DateTime UserCreated);

/// <summary>Password hashes live outside the DocType tables so no API can ever read or write them.</summary>
public interface IAuthStore
{
    Task<AuthCredential?> GetAsync(string user, CancellationToken ct = default);

    /// <param name="userCreated">
    /// The User document's creation time. A credential only counts while it matches the user document,
    /// so deleting and re-creating a user never revives an old password.
    /// </param>
    Task SetPasswordAsync(string user, string passwordHash, DateTime userCreated, DateTime now, CancellationToken ct = default);

    /// <summary>Replaces a hash with a stronger one, only if the stored hash is still <paramref name="oldHash"/>.</summary>
    Task RehashAsync(string user, string oldHash, string newHash, CancellationToken ct = default);

    Task RecordSuccessAsync(string user, DateTime now, CancellationToken ct = default);

    Task RecordFailureAsync(string user, DateTime now, int lockoutThreshold, TimeSpan lockoutDuration, CancellationToken ct = default);
}
