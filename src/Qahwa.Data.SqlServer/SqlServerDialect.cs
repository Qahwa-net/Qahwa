using Microsoft.Data.SqlClient;
using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Data;

namespace Qahwa.Data.SqlServer;

/// <summary>
/// SQL Server 2017 or later. Text is <c>nvarchar</c> (so Arabic and every other script is stored as it is typed), times are
/// <c>datetime2(6)</c> in UTC, booleans are <c>bit</c>. Searches are case-insensitive under the usual default collation; a database made
/// with a case-sensitive collation would make them case-sensitive.
/// </summary>
public sealed class SqlServerDialect : ISqlDialect
{
    public string Quote(string identifier) => "[" + identifier.Replace("]", "]]") + "]";

    public string ColumnType(FieldType type) => type switch
    {
        _ when FieldTypes.IsShortText(type) => "nvarchar(140)",
        FieldType.Int or FieldType.Rating or FieldType.Duration => "int",
        FieldType.Float => "float",
        FieldType.Decimal or FieldType.Currency or FieldType.Percent => "decimal(21,9)",
        FieldType.Check => "bit",
        FieldType.Date => "date",
        FieldType.Datetime => "datetime2(6)",
        FieldType.Time => "time(6)",
        _ when FieldTypes.HasColumn(type) => "nvarchar(max)",
        _ => throw new InvalidOperationException($"{type} fields have no column."),
    };

    /// <summary>The inspector reports a type in this same form (name and size), so a change of size is noticed too.</summary>
    public string CatalogType(FieldType type) => ColumnType(type);

    public string Parameter(int index) => "@p" + index;

    public string Page(int limit, int offset) =>
        FormattableString.Invariant($"OFFSET {Math.Max(offset, 0)} ROWS FETCH NEXT {Math.Max(limit, 1)} ROWS ONLY");

    private static string Literal(string text) => "N'" + text.Replace("'", "''") + "'";

    /// <summary>A unique index ignores empty (null) values, as PostgreSQL does, instead of allowing only one of them.</summary>
    public string CreateIndexSql(string indexName, string quotedTable, string column, bool unique) =>
        $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = {Literal(indexName)} AND object_id = OBJECT_ID({Literal(quotedTable)})) " +
        $"CREATE {(unique ? "UNIQUE " : string.Empty)}INDEX {Quote(indexName)} ON {quotedTable} ({Quote(column)})" +
        (unique ? $" WHERE {Quote(column)} IS NOT NULL" : string.Empty);

    public string DropIndexSql(string indexName, string quotedTable) => $"DROP INDEX IF EXISTS {Quote(indexName)} ON {quotedTable}";

    public string DropTableSql(string quotedTable) => $"DROP TABLE IF EXISTS {quotedTable}";

    public string AddColumnSql(string quotedTable, string columnDefinition) => $"ALTER TABLE {quotedTable} ADD {columnDefinition}";

    private static string IfMissing(string table, string create) => $"IF OBJECT_ID({Literal(table)}, N'U') IS NULL {create}";

    private static string IndexIfMissing(string table, string index, string column) =>
        $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = {Literal(index)} AND object_id = OBJECT_ID({Literal(table)})) CREATE INDEX [{index}] ON [{table}] ([{column}])";

    private static string Definition(string table, string keyColumn = "name") => IfMissing(table,
        $"CREATE TABLE [{table}] ([{keyColumn}] nvarchar(140) NOT NULL PRIMARY KEY, [definition] nvarchar(max) NOT NULL, [modified] datetime2(6) NOT NULL, [modified_by] nvarchar(140) NOT NULL)");

    public string CreateDocTypeTableSql => IfMissing("__doctype",
        "CREATE TABLE [__doctype] ([name] nvarchar(140) NOT NULL PRIMARY KEY, [definition] nvarchar(max) NOT NULL, [creation] datetime2(6) NOT NULL, " +
        "[modified] datetime2(6) NOT NULL, [owner] nvarchar(140) NOT NULL, [modified_by] nvarchar(140) NOT NULL)");

    public string CreateWebhookTableSql => Definition("__webhook_set");

    public string CreateNotificationRuleTableSql => Definition("__notification_rule");

    public string CreateAssignmentRuleTableSql => Definition("__assignment_rule");

    public string CreateWorkflowTableSql => Definition("__workflow");

    public string CreateCustomizeTableSql => Definition("__customize");

    public string CreatePermissionsTableSql => IfMissing("__permissions",
        "CREATE TABLE [__permissions] ([name] nvarchar(140) NOT NULL PRIMARY KEY, [rules] nvarchar(max) NOT NULL, [modified] datetime2(6) NOT NULL, [modified_by] nvarchar(140) NOT NULL)");

    public string CreateSeriesTableSql => IfMissing("tabSeries", "CREATE TABLE [tabSeries] ([name] nvarchar(140) NOT NULL PRIMARY KEY, [current] bigint NOT NULL DEFAULT 0)");

    public string CreateAuthTableSql => IfMissing("__auth",
        "CREATE TABLE [__auth] ([name] nvarchar(140) NOT NULL PRIMARY KEY, [password] nvarchar(max) NOT NULL, [failed_attempts] int NOT NULL DEFAULT 0, " +
        "[locked_until] datetime2(6) NULL, [last_login] datetime2(6) NULL, [password_changed] datetime2(6) NOT NULL, [user_created] datetime2(6) NOT NULL)");

    public string CreateTotpTableSql => IfMissing("__totp",
        "CREATE TABLE [__totp] ([name] nvarchar(140) NOT NULL PRIMARY KEY, [secret] nvarchar(max) NOT NULL, [enabled] bit NOT NULL DEFAULT 0, " +
        "[last_step] bigint NOT NULL DEFAULT 0, [recovery] nvarchar(max) NOT NULL DEFAULT '[]', [user_created] datetime2(6) NOT NULL)");

    public string CreateSessionTableSql => IfMissing("__sessions",
        "CREATE TABLE [__sessions] ([id] nvarchar(64) NOT NULL PRIMARY KEY, [user_name] nvarchar(140) NOT NULL, [created] datetime2(6) NOT NULL, " +
        "[last_seen] datetime2(6) NOT NULL, [expires] datetime2(6) NOT NULL, [address] nvarchar(64) NULL, [agent] nvarchar(200) NULL, [revoked] bit NOT NULL DEFAULT 0)") +
        "; " + IndexIfMissing("__sessions", "__sessions_user", "user_name");

    public string CreateIdentityTableSql => IfMissing("__identities",
        "CREATE TABLE [__identities] ([issuer] nvarchar(300) NOT NULL, [subject] nvarchar(255) NOT NULL, [user_name] nvarchar(140) NOT NULL, " +
        "[created] datetime2(6) NOT NULL, PRIMARY KEY ([issuer], [subject]))") +
        "; " + IndexIfMissing("__identities", "__identities_user", "user_name");

    public string ListSeriesSql => "SELECT [name], [current] FROM [tabSeries] ORDER BY [name]";

    // Increment in one statement under a lock that covers the gap too, so two first uses of a series cannot both insert it.
    public string NextSeriesSql =>
        "SET XACT_ABORT ON; SET NOCOUNT ON; DECLARE @v bigint; BEGIN TRAN; " +
        "UPDATE [tabSeries] WITH (UPDLOCK, HOLDLOCK) SET @v = [current] = [current] + 1 WHERE [name] = @p0; " +
        "IF @@ROWCOUNT = 0 BEGIN INSERT INTO [tabSeries] ([name], [current]) VALUES (@p0, 1); SET @v = 1; END; " +
        "COMMIT; SELECT @v;";

    public string SetSeriesSql =>
        "SET XACT_ABORT ON; SET NOCOUNT ON; BEGIN TRAN; " +
        "UPDATE [tabSeries] WITH (UPDLOCK, HOLDLOCK) SET [current] = @p1 WHERE [name] = @p0; " +
        "IF @@ROWCOUNT = 0 INSERT INTO [tabSeries] ([name], [current]) VALUES (@p0, @p1); COMMIT;";

    public string LikeEscape => " ESCAPE '\\'";

    // SQL Server allows 2,100 parameters in a statement; keep some for the other conditions.
    public int MaxParameters => 2000;

    // 2627: a unique constraint (including the primary key); 2601: a unique index; 1505: a unique index could not be made because of duplicates.
    public bool IsUniqueViolation(Exception exception) => exception is SqlException { Number: 2627 or 2601 or 1505 };

    // 2714: there is already an object with that name; 1913: the index already exists.
    public bool IsDuplicateObject(Exception exception) => exception is SqlException { Number: 2714 or 1913 };
}
