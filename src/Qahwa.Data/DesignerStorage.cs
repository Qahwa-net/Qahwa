using System.Data.Common;
using Qahwa.Core;
using Qahwa.Core.Designer;

namespace Qahwa.Data;

/// <summary>Adapts <see cref="SchemaSync"/> to the designer's <see cref="ISchemaManager"/>.</summary>
public sealed class SchemaManager(SchemaSync sync, ISqlDialect dialect) : ISchemaManager
{
    public async Task<SchemaPreview> PreviewAsync(DocTypeMeta meta, CancellationToken ct = default)
    {
        var plan = await sync.PlanAsync(meta, ct);
        return new SchemaPreview(
            plan.TableExists,
            plan.Statements.Select(s => new SchemaChange(s.Description, s.Sql)).ToList(),
            plan.Warnings);
    }

    public async Task ApplyAsync(DocTypeMeta meta, CancellationToken ct = default)
    {
        var plan = await sync.PlanAsync(meta, ct);
        try
        {
            await sync.ApplyAsync(plan, ct);
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            // Almost always a new unique field over rows that already hold duplicate values.
            throw new ConflictException(
                "A field cannot be made unique because existing documents already contain duplicate values. " +
                "Remove the duplicates first.");
        }
        catch (DbException ex) when (dialect.IsDuplicateObject(ex))
        {
            throw new ConflictException($"'{meta.Name}' was just created or changed by someone else. Reload and try again.");
        }
    }

    public Task DropAsync(DocTypeMeta meta, CancellationToken ct = default) => sync.DropTableAsync(meta, ct);
}

/// <summary>Custom DocType definitions in the <c>__doctype</c> table.</summary>
public sealed class DocTypeStore(IDbConnectionFactory connections, ISqlDialect dialect, ISchemaInspector inspector) : IDocTypeStore
{
    private string Table => dialect.Quote("__doctype");

    private string P(int i) => dialect.Parameter(i);

    private string Cols => string.Join(", ", new[] { "name", "definition", "creation", "modified", "owner", "modified_by" }.Select(dialect.Quote));

    public async Task<IReadOnlyList<StoredDocType>> ListAsync(CancellationToken ct = default)
    {
        if (await inspector.GetTableAsync("__doctype", ct) is null)
            return [];

        return await QueryAsync($"SELECT {Cols} FROM {Table} ORDER BY {dialect.Quote("name")}", [], ct);
    }

    public async Task<StoredDocType?> GetAsync(string name, CancellationToken ct = default) =>
        (await QueryAsync($"SELECT {Cols} FROM {Table} WHERE {dialect.Quote("name")} = {P(0)}", [name], ct)).FirstOrDefault();

    public async Task InsertAsync(StoredDocType row, CancellationToken ct = default)
    {
        try
        {
            await ExecuteAsync(
                $"INSERT INTO {Table} ({Cols}) VALUES ({P(0)}, {P(1)}, {P(2)}, {P(3)}, {P(4)}, {P(5)})",
                [row.Name, row.Definition, row.Creation, row.Modified, row.Owner, row.ModifiedBy],
                ct);
        }
        catch (DbException ex) when (dialect.IsUniqueViolation(ex))
        {
            throw new ConflictException($"A DocType named '{row.Name}' already exists.");
        }
    }

    public async Task<bool> UpdateAsync(StoredDocType row, DateTime expectedModified, CancellationToken ct = default) =>
        await ExecuteAsync(
            $"UPDATE {Table} SET {dialect.Quote("definition")} = {P(1)}, {dialect.Quote("modified")} = {P(2)}, " +
            $"{dialect.Quote("modified_by")} = {P(3)} WHERE {dialect.Quote("name")} = {P(0)} AND {dialect.Quote("modified")} = {P(4)}",
            [row.Name, row.Definition, row.Modified, row.ModifiedBy, expectedModified],
            ct) > 0;

    public async Task<bool> DeleteAsync(string name, CancellationToken ct = default) =>
        await ExecuteAsync($"DELETE FROM {Table} WHERE {dialect.Quote("name")} = {P(0)}", [name], ct) > 0;

    private async Task<List<StoredDocType>> QueryAsync(string sql, IReadOnlyList<object?> values, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var command = DbCommands.Create(connection, sql, values);
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<StoredDocType>();
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StoredDocType(
                reader.GetString(0),
                reader.GetString(1),
                DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc),
                DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc),
                reader.GetString(4),
                reader.GetString(5)));
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
