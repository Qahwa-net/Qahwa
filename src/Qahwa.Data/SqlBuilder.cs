using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core;

namespace Qahwa.Data;

public sealed record SqlStatement(string Sql, IReadOnlyList<object?> Parameters);

/// <summary>
/// Builds parameterized SQL for document operations. Identifiers come only from the DocType's
/// column whitelist and all values are bound as parameters, never concatenated.
/// </summary>
public sealed class SqlBuilder(ISqlDialect dialect)
{
    public SqlStatement Select(DocTypeMeta meta, ListQuery query)
    {
        var columns = meta.ColumnTypes();
        var fields = query.Fields ?? columns.Keys.ToList();
        var parameters = new List<object?>();

        var sql = new StringBuilder("SELECT ")
            .AppendJoin(", ", fields.Select(f => dialect.Quote(Require(columns, f))))
            .Append(" FROM ").Append(dialect.TableName(meta.Name));
        AppendWhere(sql, columns, query.Filters, parameters);

        var order = Require(columns, query.OrderBy ?? "modified");
        sql.Append(" ORDER BY ").Append(dialect.Quote(order)).Append(query.Descending ? " DESC" : " ASC");
        if (order != "name")
            sql.Append(", ").Append(dialect.Quote("name")).Append(" ASC"); // stable paging

        sql.Append(' ').Append(dialect.Page(query.Limit, query.Offset));
        return new SqlStatement(sql.ToString(), parameters);
    }

    public SqlStatement Count(DocTypeMeta meta, ListQuery query)
    {
        var parameters = new List<object?>();
        var sql = new StringBuilder("SELECT COUNT(*) FROM ").Append(dialect.TableName(meta.Name));
        AppendWhere(sql, meta.ColumnTypes(), query.Filters, parameters);
        return new SqlStatement(sql.ToString(), parameters);
    }

    public SqlStatement Get(DocTypeMeta meta, string name) =>
        Select(meta, new ListQuery
        {
            Filters = [new QueryFilter("name", FilterOp.Eq, name)],
            OrderBy = "name",
            Limit = 1,
        });

    public SqlStatement Exists(DocTypeMeta meta, string name)
    {
        var sql = $"SELECT 1 FROM {dialect.TableName(meta.Name)} WHERE {dialect.Quote("name")} = {dialect.Parameter(0)} {dialect.Page(1, 0)}";
        return new SqlStatement(sql, [name]);
    }

    public SqlStatement Insert(DocTypeMeta meta, IReadOnlyDictionary<string, object?> values)
    {
        var columns = meta.ColumnTypes();
        var names = values.Keys.Select(k => dialect.Quote(Require(columns, k))).ToList();
        var placeholders = Enumerable.Range(0, values.Count).Select(dialect.Parameter);
        var sql = $"INSERT INTO {dialect.TableName(meta.Name)} ({string.Join(", ", names)}) VALUES ({string.Join(", ", placeholders)})";
        return new SqlStatement(sql, values.Values.ToList());
    }

    public SqlStatement Update(
        DocTypeMeta meta,
        string name,
        IReadOnlyDictionary<string, object?> values,
        DateTime? expectedModified)
    {
        var columns = meta.ColumnTypes();
        var parameters = new List<object?>();
        var assignments = new List<string>();
        foreach (var (key, value) in values)
        {
            if (key == "name")
                throw new ArgumentException("A document cannot be renamed through update.", nameof(values));
            assignments.Add($"{dialect.Quote(Require(columns, key))} = {dialect.Parameter(parameters.Count)}");
            parameters.Add(value);
        }

        var sql = new StringBuilder($"UPDATE {dialect.TableName(meta.Name)} SET {string.Join(", ", assignments)}")
            .Append($" WHERE {dialect.Quote("name")} = {dialect.Parameter(parameters.Count)}");
        parameters.Add(name);

        if (expectedModified is not null)
        {
            sql.Append($" AND {dialect.Quote("modified")} = {dialect.Parameter(parameters.Count)}");
            parameters.Add(expectedModified);
        }

        return new SqlStatement(sql.ToString(), parameters);
    }

    public SqlStatement Delete(DocTypeMeta meta, string name) =>
        new($"DELETE FROM {dialect.TableName(meta.Name)} WHERE {dialect.Quote("name")} = {dialect.Parameter(0)}", [name]);

    private void AppendWhere(
        StringBuilder sql,
        IReadOnlyDictionary<string, FieldType> columns,
        IReadOnlyList<QueryFilter> filters,
        List<object?> parameters)
    {
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var column = dialect.Quote(Require(columns, filter.Field));
            sql.Append(i == 0 ? " WHERE " : " AND ");

            switch (filter.Op)
            {
                case FilterOp.IsNull:
                    sql.Append(column).Append(" IS NULL");
                    break;
                case FilterOp.IsNotNull:
                    sql.Append(column).Append(" IS NOT NULL");
                    break;
                case FilterOp.In:
                    var items = (filter.Value as IEnumerable<object?> ?? []).Where(v => v is not null).ToList();
                    if (items.Count == 0)
                    {
                        sql.Append("1 = 0");
                        break;
                    }

                    sql.Append(column).Append(" IN (");
                    for (var n = 0; n < items.Count; n++)
                    {
                        sql.Append(n == 0 ? string.Empty : ", ").Append(dialect.Parameter(parameters.Count));
                        parameters.Add(items[n]);
                    }

                    sql.Append(')');
                    break;
                default:
                    sql.Append(column).Append(' ').Append(Symbol(filter.Op)).Append(' ')
                        .Append(dialect.Parameter(parameters.Count));
                    parameters.Add(filter.Value);
                    break;
            }
        }
    }

    private static string Symbol(FilterOp op) => op switch
    {
        FilterOp.Eq => "=",
        FilterOp.Ne => "<>",
        FilterOp.Gt => ">",
        FilterOp.Lt => "<",
        FilterOp.Ge => ">=",
        FilterOp.Le => "<=",
        FilterOp.Like => "LIKE",
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported operator."),
    };

    private static string Require(IReadOnlyDictionary<string, FieldType> columns, string name) =>
        columns.ContainsKey(name) ? name : throw new ArgumentException($"'{name}' is not a column of this DocType.");
}
