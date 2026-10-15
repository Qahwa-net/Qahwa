using System.Data.Common;
using Qahwa.Core;

namespace Qahwa.Data;

/// <summary>Workflows in the <c>__workflow</c> table, one row per DocType.</summary>
public sealed class WorkflowStore(IDbConnectionFactory connections, ISqlDialect dialect, ISchemaInspector inspector) : IWorkflowStore
{
    private string Table => dialect.Quote("__workflow");

    private string P(int i) => dialect.Parameter(i);

    private string Cols => string.Join(", ", new[] { "name", "definition", "modified", "modified_by" }.Select(dialect.Quote));

    public async Task<IReadOnlyList<StoredWorkflow>> ListAsync(CancellationToken ct = default)
    {
        if (await inspector.GetTableAsync("__workflow", ct) is null)
            return [];

        return await QueryAsync($"SELECT {Cols} FROM {Table} ORDER BY {dialect.Quote("name")}", [], ct);
    }

    public async Task<StoredWorkflow?> GetAsync(string docType, CancellationToken ct = default)
    {
        if (await inspector.GetTableAsync("__workflow", ct) is null)
            return null;

        return (await QueryAsync($"SELECT {Cols} FROM {Table} WHERE {dialect.Quote("name")} = {P(0)}", [docType], ct)).FirstOrDefault();
    }

    public async Task<bool> SaveAsync(StoredWorkflow row, DateTime? expectedModified, CancellationToken ct = default)
    {
        if (expectedModified is null)
        {
            try
            {
                await ExecuteAsync(
                    $"INSERT INTO {Table} ({Cols}) VALUES ({P(0)}, {P(1)}, {P(2)}, {P(3)})",
                    [row.DocType, row.Definition, row.Modified, row.ModifiedBy],
                    ct);
                return true;
            }
            catch (DbException ex) when (dialect.IsUniqueViolation(ex))
            {
                return false; // someone customised it first
            }
        }

        return await ExecuteAsync(
            $"UPDATE {Table} SET {dialect.Quote("definition")} = {P(1)}, {dialect.Quote("modified")} = {P(2)}, " +
            $"{dialect.Quote("modified_by")} = {P(3)} WHERE {dialect.Quote("name")} = {P(0)} AND {dialect.Quote("modified")} = {P(4)}",
            [row.DocType, row.Definition, row.Modified, row.ModifiedBy, expectedModified],
            ct) > 0;
    }

    public async Task<bool> DeleteAsync(string docType, CancellationToken ct = default) =>
        await ExecuteAsync($"DELETE FROM {Table} WHERE {dialect.Quote("name")} = {P(0)}", [docType], ct) > 0;

    private async Task<List<StoredWorkflow>> QueryAsync(string sql, IReadOnlyList<object?> values, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<StoredWorkflow>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StoredWorkflow(
                reader.GetString(0),
                reader.GetString(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                reader.GetString(3)));
        }

        return rows;
    }

    private async Task<int> ExecuteAsync(string sql, IReadOnlyList<object?> values, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, sql, values);
        return await command.ExecuteNonQueryAsync(ct);
    }
}
