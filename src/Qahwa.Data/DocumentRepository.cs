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
    public async Task InsertAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyList<ChildChanges>? children = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await connections.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                await ExecuteAsync(connection, transaction, sql.Insert(meta, values), ct);
                await ApplyChildrenAsync(connection, transaction, children, ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            throw new ConflictException($"A {meta.Name} with the same name or a unique value already exists.");
        }
    }

    public async Task<Dictionary<string, object?>?> GetAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        (await QueryAsync(meta, sql.Get(meta, name), ct)).FirstOrDefault();

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetChildrenAsync(
        DocTypeMeta childMeta, string parentDocType, string parent, string parentField, CancellationToken ct = default) =>
        await QueryAsync(childMeta, sql.Select(childMeta, new ListQuery
        {
            Filters =
            [
                new QueryFilter("parent", FilterOp.Eq, parent),
                new QueryFilter("parenttype", FilterOp.Eq, parentDocType),
                new QueryFilter("parentfield", FilterOp.Eq, parentField),
            ],
            OrderBy = "idx",
            Descending = false,
            Limit = 1000,
        }), ct);

    public async Task<bool> UpdateAsync(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified,
        IReadOnlyList<ChildChanges>? children = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var connection = await connections.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                if (await ExecuteAsync(connection, transaction, sql.Update(meta, name, values, expectedModified), ct) == 0)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    return false;
                }

                await ApplyChildrenAsync(connection, transaction, children, ct);
                await transaction.CommitAsync(ct);
                return true;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            throw new ConflictException($"Another {meta.Name} already has the same unique value.");
        }
    }

    public async Task<bool> DeleteAsync(
        DocTypeMeta meta, string name, IReadOnlyList<DocTypeMeta>? childTables = null, CancellationToken ct = default,
        IReadOnlyList<ChildChanges>? sideWrites = null)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var child in (childTables ?? []).DistinctBy(c => c.Name))
                await ExecuteAsync(connection, transaction, sql.DeleteChildren(child, meta.Name, name), ct);

            var deleted = await ExecuteAsync(connection, transaction, sql.Delete(meta, name), ct) > 0;
            if (deleted)
                await ApplyChildrenAsync(connection, transaction, sideWrites, ct);
            await transaction.CommitAsync(ct);
            return deleted;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<bool> ExistsAsync(DocTypeMeta meta, string name, CancellationToken ct = default) =>
        await ExecuteScalarAsync(sql.Exists(meta, name), ct) is not null;

    public async Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(
        DocTypeMeta meta, ListQuery query, CancellationToken ct = default) =>
        await QueryAsync(meta, sql.Select(meta, query), ct);

    public async Task<long> CountAsync(DocTypeMeta meta, ListQuery query, CancellationToken ct = default) =>
        Convert.ToInt64(await ExecuteScalarAsync(sql.Count(meta, query), ct));

    public async Task<IReadOnlyList<GroupCount>> GroupCountAsync(
        DocTypeMeta meta, string field, IReadOnlyList<QueryFilter> filters, int limit, CancellationToken ct = default)
    {
        var statement = sql.GroupCount(meta, field, filters, limit);
        var type = meta.ColumnTypes()[field];
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, statement.Sql, statement.Parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var result = new List<GroupCount>();
        while (await reader.ReadAsync(ct))
        {
            var raw = await reader.IsDBNullAsync(0, ct) ? null : reader.GetValue(0);
            result.Add(new GroupCount(FieldValueConverter.FromStorage(type, raw), reader.GetInt64(1)));
        }

        return result;
    }

    public async Task<long> NextSeriesAsync(string prefix, CancellationToken ct = default) =>
        Convert.ToInt64(await ExecuteScalarAsync(new SqlStatement(dialect.NextSeriesSql, [prefix]), ct));

    public async Task<IReadOnlyList<SeriesCounter>> ListSeriesAsync(CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = dialect.ListSeriesSql;
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<SeriesCounter>();
        while (await reader.ReadAsync(ct))
            result.Add(new SeriesCounter(reader.GetString(0), reader.GetInt64(1)));
        return result;
    }

    public async Task SetSeriesAsync(string prefix, long current, CancellationToken ct = default) =>
        await ExecuteScalarAsync(new SqlStatement(dialect.SetSeriesSql, [prefix, current]), ct);

    public async Task RenameAsync(
        DocTypeMeta meta,
        string oldName,
        string newName,
        bool merge,
        IReadOnlyList<DocTypeMeta> childTables,
        IReadOnlyList<ReferenceUpdate> references,
        CancellationToken ct = default,
        IReadOnlyList<ChildChanges>? sideWrites = null)
    {
        try
        {
            await using var connection = await connections.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);
            try
            {
                foreach (var reference in references)
                    await ExecuteAsync(connection, transaction, sql.RenameReference(reference, oldName, newName), ct);

                if (merge)
                {
                    foreach (var child in childTables.DistinctBy(c => c.Name))
                        await ExecuteAsync(connection, transaction, sql.DeleteChildren(child, meta.Name, oldName), ct);
                    await ExecuteAsync(connection, transaction, sql.Delete(meta, oldName), ct);
                }
                else
                {
                    await ExecuteAsync(connection, transaction, sql.Rename(meta, oldName, newName), ct);
                    foreach (var child in childTables.DistinctBy(c => c.Name))
                        await ExecuteAsync(connection, transaction, sql.RenameChildren(child, meta.Name, oldName, newName), ct);
                }

                await ApplyChildrenAsync(connection, transaction, sideWrites, ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            throw new ConflictException($"A {meta.Name} named '{newName}' or with the same unique value already exists.");
        }
    }

    private async Task ApplyChildrenAsync(
        DbConnection connection, DbTransaction transaction, IReadOnlyList<ChildChanges>? children, CancellationToken ct)
    {
        foreach (var change in children ?? [])
        {
            foreach (var name in change.Deletes)
                await ExecuteAsync(connection, transaction, sql.Delete(change.Meta, name), ct);
            foreach (var update in change.Updates)
                await ExecuteAsync(connection, transaction, sql.Update(change.Meta, update.Name, update.Values, null), ct);
            foreach (var row in change.Inserts)
                await ExecuteAsync(connection, transaction, sql.Insert(change.Meta, row), ct);
        }
    }

    private static async Task<int> ExecuteAsync(
        DbConnection connection, DbTransaction transaction, SqlStatement statement, CancellationToken ct)
    {
        await using var command = DbCommands.Create(connection, statement.Sql, statement.Parameters);
        command.Transaction = transaction;
        return await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<List<Dictionary<string, object?>>> QueryAsync(DocTypeMeta meta, SqlStatement statement, CancellationToken ct)
    {
        var types = meta.ColumnTypes();
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, statement.Sql, statement.Parameters);
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

    private async Task<object?> ExecuteScalarAsync(SqlStatement statement, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, statement.Sql, statement.Parameters);
        var result = await command.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }
}
