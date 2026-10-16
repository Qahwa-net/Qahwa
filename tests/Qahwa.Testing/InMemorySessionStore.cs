using Qahwa.Core.Auth;

namespace Qahwa.Testing;

public sealed class InMemorySessionStore : ISessionStore
{
    private readonly Dictionary<string, SessionRecord> _rows = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task CreateAsync(SessionRecord session, CancellationToken ct = default)
    {
        lock (_gate)
            _rows[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task<SessionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.GetValueOrDefault(id));
    }

    public Task TouchAsync(string id, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(id, out var s))
                _rows[id] = s with { LastSeen = now };
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionRecord>> ListAsync(string user, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<SessionRecord>>(_rows.Values.Where(s => s.User == user && !s.Revoked && s.Expires > now).OrderByDescending(s => s.Created).ToList());
    }

    public Task<bool> RevokeAsync(string id, string user, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var s) || s.User != user || s.Revoked)
                return Task.FromResult(false);
            _rows[id] = s with { Revoked = true };
            return Task.FromResult(true);
        }
    }

    public Task<int> RevokeAllAsync(string user, string? exceptId, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var ids = _rows.Values.Where(s => s.User == user && !s.Revoked && s.Id != exceptId).Select(s => s.Id).ToList();
            foreach (var id in ids)
                _rows[id] = _rows[id] with { Revoked = true };
            return Task.FromResult(ids.Count);
        }
    }

    public Task DeleteExpiredAsync(DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
        {
            foreach (var id in _rows.Values.Where(s => s.Expires < now).Select(s => s.Id).ToList())
                _rows.Remove(id);
        }

        return Task.CompletedTask;
    }
}
