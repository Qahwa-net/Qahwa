using Qahwa.Core.Auth;

namespace Qahwa.Testing;

/// <summary>In-memory two-factor store with the same atomic rules as the SQL one.</summary>
public sealed class InMemoryTotpStore : ITotpStore
{
    private readonly Dictionary<string, TotpRecord> _rows = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<TotpRecord?> GetAsync(string user, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.GetValueOrDefault(user));
    }

    public Task SaveAsync(TotpRecord record, CancellationToken ct = default)
    {
        lock (_gate)
            _rows[record.User] = record;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string user, CancellationToken ct = default)
    {
        lock (_gate)
            _rows.Remove(user);
        return Task.CompletedTask;
    }

    public Task<bool> TryAdvanceStepAsync(string user, long step, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(user, out var r) || !r.Enabled || r.LastStep >= step)
                return Task.FromResult(false);
            _rows[user] = r with { LastStep = step };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryConsumeRecoveryAsync(string user, string hash, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(user, out var r) || !r.Enabled || !r.RecoveryHashes.Contains(hash))
                return Task.FromResult(false);
            _rows[user] = r with { RecoveryHashes = r.RecoveryHashes.Where(h => h != hash).ToList() };
            return Task.FromResult(true);
        }
    }
}

/// <summary>Reversible "protection" for tests: tamper-evident, so a forged token fails like the real thing.</summary>
public sealed class FakeSecretProtector : ISecretProtector
{
    private const string Prefix = "fake:";

    public string Protect(string plain) => Prefix + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plain)) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("k" + plain)))[..16];

    public string? Unprotect(string protectedText)
    {
        var parts = protectedText.Split(':');
        if (parts.Length != 3 || parts[0] + ":" != Prefix)
            return null;
        try
        {
            var plain = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            return Protect(plain) == protectedText ? plain : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
