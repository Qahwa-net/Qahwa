using System.Data.Common;
using Qahwa.Abstractions;

namespace Qahwa.Data;

/// <summary>Everything database-specific the rest of the data layer needs to know.</summary>
public interface ISqlDialect
{
    string Quote(string identifier);

    /// <summary>DDL type for a field type. Only valid for types that have a column.</summary>
    string ColumnType(FieldType type);

    /// <summary>The type name the database reports in its catalog for <see cref="ColumnType"/>.</summary>
    string CatalogType(FieldType type);

    string Parameter(int index);

    string Page(int limit, int offset);

    string CreateIndexSql(string indexName, string quotedTable, string column, bool unique);

    string CreateSeriesTableSql { get; }

    /// <summary>Table holding password hashes and login counters (see <c>IAuthStore</c>).</summary>
    string CreateAuthTableSql { get; }

    /// <summary>Increments the counter named by parameter 0 and returns the new value.</summary>
    string NextSeriesSql { get; }

    bool IsUniqueViolation(Exception exception);

    string TableName(string docType) => Quote("tab" + docType);
}

public interface IDbConnectionFactory
{
    ValueTask<DbConnection> OpenAsync(CancellationToken ct = default);
}

/// <summary>What the database currently has for one table.</summary>
public sealed record TableSchema(
    IReadOnlyDictionary<string, string> Columns,
    IReadOnlySet<string> Indexes);

public interface ISchemaInspector
{
    /// <returns>Null when the table does not exist.</returns>
    Task<TableSchema?> GetTableAsync(string tableName, CancellationToken ct = default);
}
