using System.Security.Cryptography;
using System.Text;
using Qahwa.Core;

namespace Qahwa.Data;

public sealed record SchemaStatement(string Description, string Sql);

public sealed record SchemaPlan(
    string DocType,
    string Table,
    bool TableExists,
    IReadOnlyList<SchemaStatement> Statements,
    IReadOnlyList<string> Warnings)
{
    public bool IsEmpty => Statements.Count == 0;
}

/// <summary>
/// Pure diff between a DocType's metadata and what the database has. It only ever plans additive
/// changes; anything that would alter or drop existing data is reported as a warning instead.
/// </summary>
public sealed class SchemaPlanner(ISqlDialect dialect)
{
    public SchemaPlan Plan(DocTypeMeta meta, TableSchema? current)
    {
        var table = "tab" + meta.Name;
        var quotedTable = dialect.TableName(meta.Name);
        var columns = meta.ColumnTypes();
        var statements = new List<SchemaStatement>();
        var warnings = new List<string>();

        if (current is null)
        {
            var defs = columns.Select(c => ColumnDefinition(c.Key, c.Value));
            statements.Add(new SchemaStatement(
                $"Create table {table}",
                $"CREATE TABLE {quotedTable} ({string.Join(", ", defs)})"));
        }
        else
        {
            foreach (var (name, type) in columns)
            {
                if (!current.Columns.TryGetValue(name, out var actual))
                {
                    statements.Add(new SchemaStatement(
                        $"Add column {table}.{name}",
                        $"ALTER TABLE {quotedTable} ADD COLUMN {ColumnDefinition(name, type)}"));
                }
                else if (!string.Equals(actual, dialect.CatalogType(type), StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add(
                        $"{table}.{name} is '{actual}' in the database but the DocType expects '{dialect.CatalogType(type)}'. " +
                        "Type changes are never applied automatically; write a patch.");
                }
            }
        }

        var existingIndexes = current?.Indexes ?? new HashSet<string>();
        AddIndex(statements, existingIndexes, quotedTable, table, "modified", unique: false);
        if (meta.IsChildTable)
            AddIndex(statements, existingIndexes, quotedTable, table, "parent", unique: false);

        var wantedUnique = meta.Fields.Where(f => f.Unique && FieldTypes.HasColumn(f.Type)).Select(f => f.Name).ToHashSet();
        foreach (var column in wantedUnique)
            AddIndex(statements, existingIndexes, quotedTable, table, column, unique: true);

        foreach (var column in meta.Fields.Where(f => f.Indexed && !f.Unique && FieldTypes.HasColumn(f.Type)).Select(f => f.Name))
            AddIndex(statements, existingIndexes, quotedTable, table, column, unique: false);

        // A column that is no longer unique (or no longer in the DocType) must stop enforcing uniqueness.
        // Only the index goes; the column and its data stay.
        foreach (var column in current?.Columns.Keys ?? Enumerable.Empty<string>())
        {
            var index = IndexName(table, column, unique: true);
            if (existingIndexes.Contains(index) && !wantedUnique.Contains(column))
                statements.Add(new SchemaStatement($"Drop unique index on {table}.{column}", dialect.DropIndexSql(index)));
        }

        return new SchemaPlan(meta.Name, table, current is not null, statements, warnings);
    }

    /// <summary>Index names are hashed so long DocType/field names can never collide after truncation.</summary>
    public static string IndexName(string table, string column, bool unique)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{table}.{column}")))[..16].ToLowerInvariant();
        return (unique ? "ux_" : "ix_") + hash;
    }

    private string ColumnDefinition(string name, Abstractions.FieldType type)
    {
        var def = $"{dialect.Quote(name)} {dialect.ColumnType(type)}";
        return name switch
        {
            "name" => def + " NOT NULL PRIMARY KEY",
            "docstatus" or "idx" => def + " NOT NULL DEFAULT 0",
            _ => def,
        };
    }

    private void AddIndex(
        List<SchemaStatement> statements,
        IReadOnlySet<string> existing,
        string quotedTable,
        string table,
        string column,
        bool unique)
    {
        var index = IndexName(table, column, unique);
        if (existing.Contains(index))
            return;

        statements.Add(new SchemaStatement(
            $"Create {(unique ? "unique " : string.Empty)}index on {table}.{column}",
            dialect.CreateIndexSql(index, quotedTable, column, unique)));
    }
}
