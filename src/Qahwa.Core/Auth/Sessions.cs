using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <param name="Address">Where the sign-in came from (the connection's address; forwarded headers are never trusted).</param>
/// <param name="Agent">The browser's own description, shortened.</param>
public sealed record SessionRecord(string Id, string User, DateTime Created, DateTime LastSeen, DateTime Expires, string? Address, string? Agent, bool Revoked);

public interface ISessionStore
{
    Task CreateAsync(SessionRecord session, CancellationToken ct = default);

    Task<SessionRecord?> GetAsync(string id, CancellationToken ct = default);

    Task TouchAsync(string id, DateTime now, CancellationToken ct = default);

    /// <summary>The person's sessions that are neither revoked nor expired, newest first.</summary>
    Task<IReadOnlyList<SessionRecord>> ListAsync(string user, DateTime now, CancellationToken ct = default);

    /// <returns>False when there is no such live session of that person.</returns>
    Task<bool> RevokeAsync(string id, string user, CancellationToken ct = default);

    /// <returns>How many sessions were revoked.</returns>
    Task<int> RevokeAllAsync(string user, string? exceptId, CancellationToken ct = default);

    Task DeleteExpiredAsync(DateTime now, CancellationToken ct = default);
}

/// <summary>
/// Server-side record of every sign-in. The cookie only carries a random id; whether it still counts is decided here, so a session can
/// be ended from another device, by an administrator, and it ends by itself after an absolute limit however busy it is.
/// </summary>
public sealed class SessionService(ISessionStore store, IOptions<AuthOptions> options, TimeProvider clock, SecurityPolicyService? policy = null)
{
    public const int MaxAgentLength = 200;
    private static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    public async Task<string> StartAsync(string user, string? address, string? agent, CancellationToken ct = default)
    {
        var now = Now();
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        await store.CreateAsync(new SessionRecord(id, user, now, now, now.AddDays(policy is null ? Math.Max(options.Value.AbsoluteSessionDays, 1) : await policy.AbsoluteSessionDaysAsync(ct)), Cut(address, 64), Cut(agent, MaxAgentLength), false), ct);
        if (Random.Shared.Next(50) == 0)
            await store.DeleteExpiredAsync(now.AddDays(-1), ct); // housekeeping now and then, never a separate job
        return id;
    }

    private static string? Cut(string? text, int length) => string.IsNullOrWhiteSpace(text) ? null : new string(text.Where(c => !char.IsControl(c)).Take(length).ToArray());

    /// <summary>True while the session exists, belongs to that person, is not revoked and has not passed its absolute end.</summary>
    public async Task<bool> IsValidAsync(string? id, string user, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 64)
            return false;
        var session = await store.GetAsync(id, ct);
        var now = Now();
        if (session is null || session.Revoked || session.Expires <= now || !string.Equals(session.User, user, StringComparison.Ordinal))
            return false;
        if (now - session.LastSeen > TouchEvery)
            await store.TouchAsync(id, now, ct);
        return true;
    }

    public Task<IReadOnlyList<SessionRecord>> ListAsync(string user, CancellationToken ct = default) => store.ListAsync(user, Now(), ct);

    public Task<bool> RevokeAsync(string user, string id, CancellationToken ct = default) => store.RevokeAsync(id, user, ct);

    public Task<int> RevokeOthersAsync(string user, string currentId, CancellationToken ct = default) => store.RevokeAllAsync(user, currentId, ct);

    /// <summary>Signs someone out everywhere (an administrator's tool, also used when an account is disabled).</summary>
    public Task<int> RevokeAllForAsync(string user, CancellationToken ct = default) => store.RevokeAllAsync(user, null, ct);
}
