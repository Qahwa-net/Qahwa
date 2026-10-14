using System.Data.Common;
using Qahwa.Core.Auth;

namespace Qahwa.Data;

/// <summary>Credentials and login counters in the <c>__auth</c> table.</summary>
public sealed class AuthStore(IDbConnectionFactory connections, ISqlDialect dialect) : IAuthStore
{
    private string Table => dialect.Quote("__auth");

    private string P(int index) => dialect.Parameter(index);

    public async Task<AuthCredential?> GetAsync(string user, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection,
            $"SELECT {Cols("password", "failed_attempts", "locked_until", "password_changed", "user_created")} " +
            $"FROM {Table} WHERE {dialect.Quote("name")} = {P(0)}",
            [user]);
        await using var reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
            return null;

        return new AuthCredential(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
            DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc));
    }

    public async Task SetPasswordAsync(
        string user, string passwordHash, DateTime userCreated, DateTime now, CancellationToken ct = default)
    {
        var update = $"UPDATE {Table} SET {Set("password", 1)}, {Set("failed_attempts", 2)}, " +
                     $"{dialect.Quote("locked_until")} = NULL, {Set("password_changed", 3)}, {Set("user_created", 4)} " +
                     $"WHERE {dialect.Quote("name")} = {P(0)}";
        object?[] values = [user, passwordHash, 0, now, userCreated];

        if (await ExecuteAsync(update, values, ct) > 0)
            return;

        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} ({Cols("name", "password", "failed_attempts", "password_changed", "user_created")}) " +
                $"VALUES ({P(0)}, {P(1)}, {P(2)}, {P(3)}, {P(4)})",
                values,
                ct);
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            // Another request created the row between our UPDATE and INSERT; update it instead.
            await ExecuteAsync(update, values, ct);
        }
    }

    public async Task RehashAsync(string user, string oldHash, string newHash, CancellationToken ct = default) =>
        await ExecuteAsync(
            $"UPDATE {Table} SET {Set("password", 2)} WHERE {dialect.Quote("name")} = {P(0)} AND {dialect.Quote("password")} = {P(1)}",
            [user, oldHash, newHash],
            ct);

    public async Task RecordSuccessAsync(string user, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync(
            $"UPDATE {Table} SET {Set("failed_attempts", 2)}, {dialect.Quote("locked_until")} = NULL, {Set("last_login", 1)} " +
            $"WHERE {dialect.Quote("name")} = {P(0)}",
            [user, now, 0],
            ct);

    /// <summary>
    /// One atomic statement: the counter restarts if a previous lock has expired, and the account locks
    /// when the new count reaches the threshold.
    /// </summary>
    public async Task RecordFailureAsync(
        string user, DateTime now, int lockoutThreshold, TimeSpan lockoutDuration, CancellationToken ct = default)
    {
        var attempts = dialect.Quote("failed_attempts");
        var locked = dialect.Quote("locked_until");
        var expired = $"({locked} IS NOT NULL AND {locked} <= {P(1)})";
        var next = $"(CASE WHEN {expired} THEN 1 ELSE {attempts} + 1 END)";

        await ExecuteAsync(
            $"UPDATE {Table} SET {attempts} = {next}, " +
            $"{locked} = CASE WHEN {next} >= {P(2)} THEN {P(3)} WHEN {expired} THEN NULL ELSE {locked} END " +
            $"WHERE {dialect.Quote("name")} = {P(0)}",
            [user, now, lockoutThreshold, now + lockoutDuration],
            ct);
    }

    private string Cols(params string[] names) => string.Join(", ", names.Select(dialect.Quote));

    private string Set(string column, int parameter) => $"{dialect.Quote(column)} = {P(parameter)}";

    private async Task<int> ExecuteAsync(string sql, IReadOnlyList<object?> values, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
