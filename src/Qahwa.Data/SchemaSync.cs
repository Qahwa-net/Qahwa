using Qahwa.Core;

namespace Qahwa.Data;

/// <summary>Plans and applies schema changes for DocTypes against a live database.</summary>
public sealed class SchemaSync(
    IDbConnectionFactory connections,
    ISqlDialect dialect,
    ISchemaInspector inspector)
{
    private readonly SchemaPlanner _planner = new(dialect);

    public async Task<SchemaPlan> PlanAsync(DocTypeMeta meta, CancellationToken ct = default)
    {
        var current = await inspector.GetTableAsync("tab" + meta.Name, ct);
        return _planner.Plan(meta, current);
    }

    /// <summary>Creates framework tables that are not DocTypes (naming series counters, credentials).</summary>
    public async Task EnsureSystemTablesAsync(CancellationToken ct = default) =>
        await ExecuteAsync([dialect.CreateSeriesTableSql, dialect.CreateAuthTableSql, dialect.CreateDocTypeTableSql, dialect.CreatePermissionsTableSql, dialect.CreateCustomizeTableSql], ct);

    /// <summary>Drops the DocType's table. Callers must have checked that it holds no documents.</summary>
    public Task DropTableAsync(DocTypeMeta meta, CancellationToken ct = default) =>
        ExecuteAsync([dialect.DropTableSql(dialect.TableName(meta.Name))], ct);

    public Task ApplyAsync(SchemaPlan plan, CancellationToken ct = default) =>
        plan.IsEmpty ? Task.CompletedTask : ExecuteAsync(plan.Statements.Select(s => s.Sql), ct);

    // One transaction per plan: PostgreSQL DDL is transactional, so a failure leaves the table untouched.
    private async Task ExecuteAsync(IEnumerable<string> statements, CancellationToken ct)
    {
        await using var connection = await connections.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
