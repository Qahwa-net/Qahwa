using Qahwa.Core.Auth;

namespace Qahwa.Data;

/// <summary>Which provider identity is which user, in the <c>__identities</c> table.</summary>
public sealed class IdentityStore(IDbConnectionFactory connections, ISqlDialect dialect) : IIdentityStore
{
    private string Table => dialect.Quote("__identities");

    private string P(int index) => dialect.Parameter(index);

    private string Q(string column) => dialect.Quote(column);

    public async Task<string?> FindUserAsync(string issuer, string subject, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, $"SELECT {Q("user_name")} FROM {Table} WHERE {Q("issuer")} = {P(0)} AND {Q("subject")} = {P(1)}", [issuer, subject]);
        return await command.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<string>> SubjectsOfAsync(string issuer, string user, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, $"SELECT {Q("subject")} FROM {Table} WHERE {Q("issuer")} = {P(0)} AND {Q("user_name")} = {P(1)}", [issuer, user]);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var list = new List<string>();
        while (await reader.ReadAsync(ct))
            list.Add(reader.GetString(0));
        return list;
    }

    public async Task<bool> LinkAsync(string issuer, string subject, string user, DateTime now, CancellationToken ct = default)
    {
        try
        {
            await using var connection = await connections.OpenAsync(ct);
            await using var command = DbCommands.Create(connection,
                $"INSERT INTO {Table} ({Q("issuer")}, {Q("subject")}, {Q("user_name")}, {Q("created")}) VALUES ({P(0)}, {P(1)}, {P(2)}, {P(3)})", [issuer, subject, user, now]);
            await command.ExecuteNonQueryAsync(ct);
            return true;
        }
        catch (System.Data.Common.DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            return false;
        }
    }
}
