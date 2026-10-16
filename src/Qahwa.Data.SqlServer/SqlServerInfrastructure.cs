using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Data;

namespace Qahwa.Data.SqlServer;

public sealed class SqlServerConnectionFactory(string connectionString) : IDbConnectionFactory
{
    public async ValueTask<DbConnection> OpenAsync(CancellationToken ct = default)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}

public sealed class SqlServerSchemaInspector(IDbConnectionFactory connections) : ISchemaInspector
{
    public async Task<TableSchema?> GetTableAsync(string tableName, CancellationToken ct = default)
    {
        await using var connection = await connections.OpenAsync(ct);

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = Create(connection,
            "SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, DATETIME_PRECISION FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = SCHEMA_NAME() AND TABLE_NAME = @p0", tableName))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                columns[reader.GetString(0)] = TypeName(reader);
        }

        if (columns.Count == 0)
            return null;

        var indexes = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = Create(connection,
            "SELECT i.name FROM sys.indexes i WHERE i.object_id = OBJECT_ID(QUOTENAME(SCHEMA_NAME()) + N'.' + QUOTENAME(@p0)) AND i.name IS NOT NULL", tableName))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
                indexes.Add(reader.GetString(0));
        }

        return new TableSchema(columns, indexes);
    }

    /// <summary>The type as the dialect writes it: <c>nvarchar(140)</c>, <c>nvarchar(max)</c>, <c>decimal(21,9)</c>, <c>datetime2(6)</c>, <c>int</c>...</summary>
    private static string TypeName(DbDataReader r)
    {
        var type = r.GetString(1);
        return type switch
        {
            "nvarchar" or "varchar" => $"{type}({(r.IsDBNull(2) || r.GetInt32(2) < 0 ? "max" : r.GetInt32(2).ToString(System.Globalization.CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{type}({Convert.ToInt32(r.GetValue(3), System.Globalization.CultureInfo.InvariantCulture)},{Convert.ToInt32(r.GetValue(4), System.Globalization.CultureInfo.InvariantCulture)})",
            "datetime2" or "time" => $"{type}({Convert.ToInt32(r.GetValue(5), System.Globalization.CultureInfo.InvariantCulture)})",
            _ => type,
        };
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

public static class SqlServerServiceCollectionExtensions
{
    /// <param name="connectionString">Resolved lazily so configuration overrides (e.g. in tests) are honored.</param>
    public static IServiceCollection AddQahwaSqlServer(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddSingleton<ISqlDialect, SqlServerDialect>();
        services.AddSingleton<IDbConnectionFactory>(sp => new SqlServerConnectionFactory(connectionString(sp)));
        services.AddSingleton<ISchemaInspector, SqlServerSchemaInspector>();
        return services;
    }
}
