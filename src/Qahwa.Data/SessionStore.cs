using Qahwa.Core.Auth;

namespace Qahwa.Data;

/// <summary>Sign-in sessions in the <c>__sessions</c> table.</summary>
public sealed class SessionStore(IDbConnectionFactory connections, ISqlDialect dialect) : ISessionStore
{
    private string Table => dialect.Quote("__sessions");

    private string P(int index) => dialect.Parameter(index);

    private string Q(string column) => dialect.Quote(column);

    private string Columns => string.Join(", ", new[] { "id", "user_name", "created", "last_seen", "expires", "address", "agent", "revoked" }.Select(Q));

    private static SessionRecord Read(System.Data.Common.DbDataReader r) => new(
        r.GetString(0), r.GetString(1),
        DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(3), DateTimeKind.Utc), DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc),
        r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetBoolean(7));

    public async Task CreateAsync(SessionRecord s, CancellationToken ct = default) =>
        await ExecuteAsync($"INSERT INTO {Table} ({Columns}) VALUES ({P(0)}, {P(1)}, {P(2)}, {P(3)}, {P(4)}, {P(5)}, {P(6)}, {P(7)})",
            [s.Id, s.User, s.Created, s.LastSeen, s.Expires, s.Address, s.Agent, s.Revoked], ct);

    public async Task<SessionRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, $"SELECT {Columns} FROM {Table} WHERE {Q("id")} = {P(0)}", [id]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task TouchAsync(string id, DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync($"UPDATE {Table} SET {Q("last_seen")} = {P(1)} WHERE {Q("id")} = {P(0)}", [id, now], ct);

    public async Task<IReadOnlyList<SessionRecord>> ListAsync(string user, DateTime now, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection,
            $"SELECT {Columns} FROM {Table} WHERE {Q("user_name")} = {P(0)} AND {Q("revoked")} = {P(2)} AND {Q("expires")} > {P(1)} ORDER BY {Q("created")} DESC LIMIT 100",
            [user, now, false]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<SessionRecord>();
        while (await reader.ReadAsync(ct))
            list.Add(Read(reader));
        return list;
    }

    public async Task<bool> RevokeAsync(string id, string user, CancellationToken ct = default) =>
        await ExecuteAsync($"UPDATE {Table} SET {Q("revoked")} = {P(2)} WHERE {Q("id")} = {P(0)} AND {Q("user_name")} = {P(1)} AND {Q("revoked")} = {P(3)}", [id, user, true, false], ct) > 0;

    public async Task<int> RevokeAllAsync(string user, string? exceptId, CancellationToken ct = default) =>
        await ExecuteAsync(
            $"UPDATE {Table} SET {Q("revoked")} = {P(1)} WHERE {Q("user_name")} = {P(0)} AND {Q("revoked")} = {P(2)} AND {Q("id")} <> {P(3)}",
            [user, true, false, exceptId ?? string.Empty], ct);

    public async Task DeleteExpiredAsync(DateTime now, CancellationToken ct = default) =>
        await ExecuteAsync($"DELETE FROM {Table} WHERE {Q("expires")} < {P(0)}", [now], ct);

    private async Task<int> ExecuteAsync(string sql, IReadOnlyList<object?> values, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
