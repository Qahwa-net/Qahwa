using Npgsql;
using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Data;

namespace Qahwa.Data.Postgres;

public sealed class PostgresDialect : ISqlDialect
{
    public string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

    public string ColumnType(FieldType type) => type switch
    {
        _ when FieldTypes.IsShortText(type) => "varchar(140)",
        FieldType.Int => "integer",
        FieldType.Float => "double precision",
        FieldType.Decimal or FieldType.Currency or FieldType.Percent => "numeric(21,9)",
        FieldType.Check => "boolean",
        FieldType.Date => "date",
        FieldType.Datetime => "timestamp with time zone",
        FieldType.Time => "time without time zone",
        _ when FieldTypes.HasColumn(type) => "text",
        _ => throw new InvalidOperationException($"{type} fields have no column."),
    };

    public string CatalogType(FieldType type) => ColumnType(type) switch
    {
        "varchar(140)" => "character varying",
        "numeric(21,9)" => "numeric",
        var other => other,
    };

    public string Parameter(int index) => "@p" + index;

    public string Page(int limit, int offset) =>
        FormattableString.Invariant($"LIMIT {Math.Max(limit, 1)} OFFSET {Math.Max(offset, 0)}");

    public string CreateIndexSql(string indexName, string quotedTable, string column, bool unique) =>
        $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX IF NOT EXISTS {Quote(indexName)} ON {quotedTable} ({Quote(column)})";

    public string CreateSeriesTableSql =>
        "CREATE TABLE IF NOT EXISTS \"tabSeries\" (\"name\" varchar(140) NOT NULL PRIMARY KEY, \"current\" bigint NOT NULL DEFAULT 0)";

    public string CreateAuthTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__auth\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"password\" text NOT NULL, " +
        "\"failed_attempts\" integer NOT NULL DEFAULT 0, " +
        "\"locked_until\" timestamp with time zone, " +
        "\"last_login\" timestamp with time zone, " +
        "\"password_changed\" timestamp with time zone NOT NULL, " +
        "\"user_created\" timestamp with time zone NOT NULL)";

    public string NextSeriesSql =>
        "INSERT INTO \"tabSeries\" (\"name\", \"current\") VALUES (@p0, 1) " +
        "ON CONFLICT (\"name\") DO UPDATE SET \"current\" = \"tabSeries\".\"current\" + 1 RETURNING \"current\"";

    public bool IsUniqueViolation(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
