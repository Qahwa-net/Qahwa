using System.Data.Common;
using Qahwa.Abstractions;
using Qahwa.Core;

namespace Qahwa.Data;

/// <summary>ADO.NET implementation of <see cref="IDocumentRepository"/> (no ORM; schemas are dynamic).</summary>
public sealed class DocumentRepository(
    IDbConnectionFactory connections,
    ISqlDialect dialect,
    SqlBuilder sql) : IDocumentRepository
{
    public async Task InsertAsync(DocTypeMeta meta, IReadOnlyDictionary<string, object?> values, CancellationToken ct = default)
    {
        try
        {
            await ExecuteNonQueryAsync(sql.Insert(meta, values), ct);
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            throw new ConflictException($"A {meta.Name} with the same name or a unique value already exists.");
        }
    }

    public async Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        (await QueryAsync(meta, sql.Get(meta, name), ct)).FirstOrDefault();

    public async Task<bool> UpdateAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified,
        CancellationToken ct = default)
    {
        try
        {
            return await ExecuteNonQueryAsync(sql.Update(meta, name, values, expectedModified), ct) > 0;
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            throw new ConflictException($"Another {meta.Name} already has the same unique value.");
        }
    }

    public async Task<bool> DeleteAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        await ExecuteNonQueryAsync(sql.Delete(meta, name), ct) > 0;

    public async Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        await ExecuteScalarAsync(sql.Exists(meta, name), ct) is not null;

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(
        DocTypeMeta meta, ListQuery query, CancellationToken ct = default) =>
        await QueryAsync(meta, sql.Select(meta, query), ct);

    public async Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default) =>
        Convert.ToInt64(await ExecuteScalarAsync(sql.Count(meta, query), ct));

    public async Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default) =>
        Convert.ToInt64(await ExecuteScalarAsync(new SqlStatement(dialect.NextSeriesSql, [prefix]), ct));

    private async Task<List<Dictionary<string, object?>>> QueryAsync(DocTypeMeta meta, SqlStatement statement, CancellationToken ct)
    {
        var types = meta.ColumnTypes();
        await using var connection = await connections.OpenAsync(ct);
        await using var command = CreateCommand(connection, statement);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var column = reader.GetName(i);
                var raw = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                row[column] = types.TryGetValue(column, out var type) ? FieldValueConverter.FromStorage(type, raw) : raw;
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<int> ExecuteNonQueryAsync(SqlStatement statement, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = CreateCommand(connection, statement);
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> ExecuteScalarAsync(SqlStatement statement, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = CreateCommand(connection, statement);
        var result = await command.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }

    private static DbCommand CreateCommand(DbConnection connection, SqlStatement statement)
    {
        var command = connection.CreateCommand();
        command.CommandText = statement.Sql;
        for (var i = 0; i < statement.Parameters.Count; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{i}";
            parameter.Value = statement.Parameters[i] ?? DBNull.Value;
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
