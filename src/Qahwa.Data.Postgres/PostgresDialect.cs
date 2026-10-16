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
        FieldType.Int or FieldType.Rating or FieldType.Duration => "integer",
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

    public string DropIndexSql(string indexName) => $"DROP INDEX IF EXISTS {Quote(indexName)}";

    public string DropTableSql(string quotedTable) => $"DROP TABLE IF EXISTS {quotedTable}";

    public string CreateDocTypeTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__doctype\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"definition\" text NOT NULL, " +
        "\"creation\" timestamp with time zone NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"owner\" varchar(140) NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

    public string CreateWebhookTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__webhook_set\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"definition\" text NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

    public string CreateNotificationRuleTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__notification_rule\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"definition\" text NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

    public string CreateAssignmentRuleTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__assignment_rule\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"definition\" text NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

    public string CreateWorkflowTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__workflow\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"definition\" text NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

    public string CreateCustomizeTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__customize\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"definition\" text NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

    public string CreatePermissionsTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__permissions\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"rules\" text NOT NULL, " +
        "\"modified\" timestamp with time zone NOT NULL, " +
        "\"modified_by\" varchar(140) NOT NULL)";

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

    public string CreateTotpTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__totp\" (" +
        "\"name\" varchar(140) NOT NULL PRIMARY KEY, " +
        "\"secret\" text NOT NULL, " +
        "\"enabled\" boolean NOT NULL DEFAULT false, " +
        "\"last_step\" bigint NOT NULL DEFAULT 0, " +
        "\"recovery\" text NOT NULL DEFAULT '[]', " +
        "\"user_created\" timestamp with time zone NOT NULL)";

    public string CreateSessionTableSql =>
        "CREATE TABLE IF NOT EXISTS \"__sessions\" (" +
        "\"id\" varchar(64) NOT NULL PRIMARY KEY, " +
        "\"user_name\" varchar(140) NOT NULL, " +
        "\"created\" timestamp with time zone NOT NULL, " +
        "\"last_seen\" timestamp with time zone NOT NULL, " +
        "\"expires\" timestamp with time zone NOT NULL, " +
        "\"address\" varchar(64), " +
        "\"agent\" varchar(200), " +
        "\"revoked\" boolean NOT NULL DEFAULT false); " +
        "CREATE INDEX IF NOT EXISTS \"__sessions_user\" ON \"__sessions\" (\"user_name\")";

    public string LikeOperator => "ILIKE";

    public string ListSeriesSql => "SELECT \"name\", \"current\" FROM \"tabSeries\" ORDER BY \"name\"";

    public string SetSeriesSql =>
        "INSERT INTO \"tabSeries\" (\"name\", \"current\") VALUES (@p0, @p1) ON CONFLICT (\"name\") DO UPDATE SET \"current\" = EXCLUDED.\"current\"";

    public string NextSeriesSql =>
        "INSERT INTO \"tabSeries\" (\"name\", \"current\") VALUES (@p0, 1) " +
        "ON CONFLICT (\"name\") DO UPDATE SET \"current\" = \"tabSeries\".\"current\" + 1 RETURNING \"current\"";

    public bool IsDuplicateObject(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.DuplicateTable or PostgresErrorCodes.DuplicateObject };

    public bool IsUniqueViolation(Exception exception) =>
        exception is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
