using System.Text.Json;
using Qahwa.Core.Auth;

namespace Qahwa.Data;

/// <summary>Two-factor secrets (encrypted by the caller) and recovery code hashes in the <c>__totp</c> table.</summary>
public sealed class TotpStore(IDbConnectionFactory connections, ISqlDialect dialect) : ITotpStore
{
    private string Table => dialect.Quote("__totp");

    private string P(int index) => dialect.Parameter(index);

    private string Q(string column) => dialect.Quote(column);

    public async Task<TotpRecord?> GetAsync(string user, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection,
            $"SELECT {Q("secret")}, {Q("enabled")}, {Q("last_step")}, {Q("recovery")}, {Q("user_created")} FROM {Table} WHERE {Q("name")} = {P(0)}", [user]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        return new TotpRecord(
            user, reader.GetString(0), reader.GetBoolean(1), reader.GetInt64(2),
            JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? [],
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    public async Task SaveAsync(TotpRecord record, CancellationToken ct = default)
    {
        var recovery = JsonSerializer.Serialize(record.RecoveryHashes);
        object?[] values = [record.User, record.Secret, record.Enabled, record.LastStep, recovery, record.UserCreated];
        var update = $"UPDATE {Table} SET {Q("secret")} = {P(1)}, {Q("enabled")} = {P(2)}, {Q("last_step")} = {P(3)}, {Q("recovery")} = {P(4)}, {Q("user_created")} = {P(5)} WHERE {Q("name")} = {P(0)}";
        if (await ExecuteAsync(update, values, ct) > 0)
            return;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} ({Q("name")}, {Q("secret")}, {Q("enabled")}, {Q("last_step")}, {Q("recovery")}, {Q("user_created")}) VALUES ({P(0)}, {P(1)}, {P(2)}, {P(3)}, {P(4)}, {P(5)})",
                values, ct);
        }
        catch (System.Data.Common.DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            await ExecuteAsync(update, values, ct);
        }
    }

    public async Task DeleteAsync(string user, CancellationToken ct = default) =>
        await ExecuteAsync($"DELETE FROM {Table} WHERE {Q("name")} = {P(0)}", [user], ct);

    /// <summary>One statement, so two requests with the same code cannot both succeed.</summary>
    public async Task<bool> TryAdvanceStepAsync(string user, long step, CancellationToken ct = default) =>
        await ExecuteAsync($"UPDATE {Table} SET {Q("last_step")} = {P(1)} WHERE {Q("name")} = {P(0)} AND {Q("enabled")} = {P(2)} AND {Q("last_step")} < {P(1)}", [user, step, true], ct) > 0;

    public async Task<bool> TryConsumeRecoveryAsync(string user, string hash, CancellationToken ct = default)
    {
        // Compare-and-swap on the whole list: if another request changed it meanwhile, read again.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var record = await GetAsync(user, ct);
            if (record is not { Enabled: true } || !record.RecoveryHashes.Contains(hash, StringComparer.Ordinal))
                return false;

            var before = JsonSerializer.Serialize(record.RecoveryHashes);
            var after = JsonSerializer.Serialize(record.RecoveryHashes.Where(h => h != hash).ToList());
            if (await ExecuteAsync($"UPDATE {Table} SET {Q("recovery")} = {P(2)} WHERE {Q("name")} = {P(0)} AND {Q("recovery")} = {P(1)}", [user, before, after], ct) > 0)
                return true;
        }

        return false;
    }

    private async Task<int> ExecuteAsync(string sql, IReadOnlyList<object?> values, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
