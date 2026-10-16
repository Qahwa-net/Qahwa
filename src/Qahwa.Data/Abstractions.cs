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

    /// <param name="quotedTable">The table the index belongs to (some databases need it to drop an index).</param>
    string DropIndexSql(string indexName, string quotedTable);

    /// <summary>Adds one column (already quoted and typed, e.g. <c>"since" date</c>) to a table.</summary>
    string AddColumnSql(string quotedTable, string columnDefinition);

    string DropTableSql(string quotedTable);

    /// <summary>Table holding DocType definitions created in the designer.</summary>
    string CreateDocTypeTableSql { get; }

    /// <summary>Table holding permission rules customised in the Role Permission Manager.</summary>
    string CreatePermissionsTableSql { get; }

    /// <summary>Table holding customisations of standard DocTypes made in the Customize Form.</summary>
    string CreateCustomizeTableSql { get; }

    /// <summary>Table holding webhooks.</summary>
    string CreateWebhookTableSql { get; }

    /// <summary>Table holding notification rules.</summary>
    string CreateNotificationRuleTableSql { get; }

    /// <summary>Table holding assignment rules.</summary>
    string CreateAssignmentRuleTableSql { get; }

    /// <summary>Table holding workflows built in the Workflow Builder.</summary>
    string CreateWorkflowTableSql { get; }

    string CreateSeriesTableSql { get; }

    /// <summary>Table holding password hashes and login counters (see <c>IAuthStore</c>).</summary>
    string CreateAuthTableSql { get; }

    /// <summary>Table holding two-factor secrets and recovery codes (see <c>ITotpStore</c>).</summary>
    string CreateTotpTableSql { get; }

    /// <summary>Table holding sign-in sessions (see <c>ISessionStore</c>).</summary>
    string CreateSessionTableSql { get; }

    /// <summary>Table linking identity-provider identities to users (see <c>IIdentityStore</c>).</summary>
    string CreateIdentityTableSql { get; }

    /// <summary>Increments the counter named by parameter 0 and returns the new value.</summary>
    string NextSeriesSql { get; }

    /// <summary>Lists every counter: prefix, current.</summary>
    string ListSeriesSql { get; }

    /// <summary>Sets the counter named by parameter 0 to parameter 1.</summary>
    string SetSeriesSql { get; }

    /// <summary>The most values one statement may bind (a longer list of values is refused with a clear message instead of a database error).</summary>
    int MaxParameters => 30_000;

    bool IsUniqueViolation(Exception exception);

    /// <summary>The table or index being created already exists (another instance got there first).</summary>
    bool IsDuplicateObject(Exception exception);

    /// <summary>The text-matching operator. Searches are case-insensitive, so a typed "acme" finds "Acme Ltd".</summary>
    string LikeOperator => "LIKE";

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
