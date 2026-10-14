using Qahwa.Core.Auth;

namespace Qahwa.Testing;

/// <summary>In-memory credential store with the same counter and lockout rules as the SQL implementation.</summary>
public sealed class InMemoryAuthStore : IAuthStore
{
    private sealed class Row
    {
        public required string Hash { get; set; }
        public int Failed { get; set; }
        public DateTime? Locked { get; set; }
        public DateTime Changed { get; set; }
        public DateTime UserCreated { get; set; }
    }

    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<AuthCredential?> GetAsync(string user, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue(user, out var r)
                ? new AuthCredential(r.Hash, r.Failed, r.Locked, r.Changed, r.UserCreated)
                : null);
        }
    }

    public Task SetPasswordAsync(string user, string passwordHash, DateTime userCreated, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
            _rows[user] = new Row { Hash = passwordHash, Changed = now, UserCreated = userCreated };
        return Task.CompletedTask;
    }

    public Task RehashAsync(string user, string oldHash, string newHash, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(user, out var r) && r.Hash == oldHash)
                r.Hash = newHash;
        }

        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(string user, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(user, out var r))
            {
                r.Failed = 0;
                r.Locked = null;
            }
        }

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(string user, DateTime now, int lockoutThreshold, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(user, out var r))
                return Task.CompletedTask;

            var expired = r.Locked is not null && r.Locked <= now;
            var next = expired ? 1 : r.Failed + 1;
            r.Locked = next >= lockoutThreshold ? now + lockoutDuration : expired ? null : r.Locked;
            r.Failed = next;
        }

        return Task.CompletedTask;
    }
}
