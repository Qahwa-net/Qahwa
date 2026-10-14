using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Qahwa.Data;

namespace Qahwa.Data.Postgres;

public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory, IAsyncDisposable
{
    public async ValueTask<DbConnection> OpenAsync(CancellationToken ct = default) =>
        await dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();
}

public sealed class PostgresSchemaInspector(IDbConnectionFactory connections) : ISchemaInspector
{
    public async Task<TableSchema?> GetTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = Create(connection,
            "SELECT column_name, data_type FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND table_name = @p0", tableName))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                columns[reader.GetString(0)] = reader.GetString(1);
        }

        if (columns.Count == 0)
            return null;

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Create(connection,
            "SELECT indexname FROM pg_indexes WHERE schemaname = current_schema() AND tablename = @p0", tableName))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                indexes.Add(reader.GetString(0));
        }

        return new TableSchema(columns, indexes);
    }

    private static DbCommand Create(DbConnection connection, string sql, string tableName)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "p0";
        parameter.Value = tableName;
        command.Parameters.Add(parameter);
        return command;
    }
}

public static class PostgresServiceCollectionExtensions
{
    /// <param name="connectionString">Resolved lazily so configuration overrides (e.g. in tests) are honored.</param>
    public static IServiceCollection AddQahwaPostgres(
        this IServiceCollection services,
        Func<IServiceProvider, string> connectionString)
    {
        services.AddSingleton<ISqlDialect, PostgresDialect>();
        services.AddSingleton<IDbConnectionFactory>(sp =>
            new NpgsqlConnectionFactory(NpgsqlDataSource.Create(connectionString(sp))));
        services.AddSingleton<ISchemaInspector, PostgresSchemaInspector>();
        return services;
    }
}
