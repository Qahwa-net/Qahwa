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

    /// <summary>Counts documents per value of one column. The column must belong to the DocType (whitelist); values are parameters.</summary>
    public SqlStatement GroupCount(DocTypeMeta meta, string field, IReadOnlyList<QueryFilter> filters, int limit)
    {
        var columns = meta.ColumnTypes();
        var column = dialect.Quote(Require(columns, field));
        var parameters = new List<object?>();
        var sql = new StringBuilder("SELECT ").Append(column).Append(", COUNT(*) FROM ").Append(dialect.TableName(meta.Name));
        AppendWhere(sql, columns, filters, parameters);
        sql.Append(" GROUP BY ").Append(column).Append(" ORDER BY COUNT(*) DESC, ").Append(column).Append(" ASC ").Append(dialect.Page(limit, 0));
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
        var sql = $"SELECT 1 FROM {dialect.TableName(meta.Name)} WHERE {dialect.Quote("name")} = {dialect.Parameter(0)} ORDER BY {dialect.Quote("name")} {dialect.Page(1, 0)}";
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

    /// <summary>Removes every row of a child table that belongs to one parent document.</summary>
    public SqlStatement DeleteChildren(DocTypeMeta childMeta, string parentDocType, string parent) =>
        new($"DELETE FROM {dialect.TableName(childMeta.Name)} WHERE {dialect.Quote("parent")} = {dialect.Parameter(0)} " +
            $"AND {dialect.Quote("parenttype")} = {dialect.Parameter(1)}",
            [parent, parentDocType]);

    /// <summary>Gives a document a new name (the primary key). Every identifier comes from the DocType, every value is a parameter.</summary>
    public SqlStatement Rename(DocTypeMeta meta, string oldName, string newName) =>
        new($"UPDATE {dialect.TableName(meta.Name)} SET {dialect.Quote("name")} = {dialect.Parameter(0)} WHERE {dialect.Quote("name")} = {dialect.Parameter(1)}",
            [newName, oldName]);

    /// <summary>Points the child rows of a renamed parent at its new name.</summary>
    public SqlStatement RenameChildren(DocTypeMeta childMeta, string parentDocType, string oldName, string newName) =>
        new($"UPDATE {dialect.TableName(childMeta.Name)} SET {dialect.Quote("parent")} = {dialect.Parameter(0)} " +
            $"WHERE {dialect.Quote("parent")} = {dialect.Parameter(1)} AND {dialect.Quote("parenttype")} = {dialect.Parameter(2)}",
            [newName, oldName, parentDocType]);

    /// <summary>Updates a column that holds the name of another document. The column must exist in the DocType (whitelist).</summary>
    public SqlStatement RenameReference(ReferenceUpdate reference, string oldName, string newName)
    {
        var columns = reference.Meta.ColumnTypes();
        var sql = new StringBuilder("UPDATE ")
            .Append(dialect.TableName(reference.Meta.Name))
            .Append(" SET ").Append(dialect.Quote(Require(columns, reference.Field))).Append(" = ").Append(dialect.Parameter(0))
            .Append(" WHERE ").Append(dialect.Quote(reference.Field)).Append(" = ").Append(dialect.Parameter(1));
        List<object?> parameters = [newName, oldName];
        if (reference.WhereField is not null)
        {
            sql.Append(" AND ").Append(dialect.Quote(Require(columns, reference.WhereField))).Append(" = ").Append(dialect.Parameter(2));
            parameters.Add(reference.WhereValue);
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
            sql.Append(i == 0 ? " WHERE " : " AND ");
            AppendCondition(sql, columns, filters[i], parameters);
        }

        CheckParameterCount(parameters.Count);
    }

    /// <summary>One filter as a SQL condition. Identifiers come from the DocType's columns, every value is a parameter.</summary>
    private void AppendCondition(
        StringBuilder sql,
        IReadOnlyDictionary<string, FieldType> columns,
        QueryFilter filter,
        List<object?> parameters)
    {
        {
            var column = dialect.Quote(Require(columns, filter.Field));

            switch (filter.Op)
            {
                case FilterOp.ChildMatches:
                    var child = (ChildMatch)filter.Value!;
                    var childColumns = child.Child.ColumnTypes();
                    sql.Append(column).Append(" IN (SELECT ").Append(dialect.Quote(Require(childColumns, "parent"))).Append(" FROM ")
                        .Append(dialect.TableName(child.Child.Name)).Append(" WHERE ").Append(dialect.Quote(Require(childColumns, "parenttype"))).Append(" = ")
                        .Append(dialect.Parameter(parameters.Count)).Append(" AND ").Append(dialect.Quote(Require(childColumns, "parentfield"))).Append(" = ")
                        .Append(dialect.Parameter(parameters.Count + 1));
                    parameters.Add(child.ParentDocType);
                    parameters.Add(child.ParentField);
                    foreach (var inner in child.Inner)
                    {
                        sql.Append(" AND ");
                        AppendCondition(sql, childColumns, inner, parameters);
                    }

                    sql.Append(')');
                    break;
                case FilterOp.IsNull:
                    sql.Append(column).Append(" IS NULL");
                    break;
                case FilterOp.IsNotNull:
                    sql.Append(column).Append(" IS NOT NULL");
                    break;
                case FilterOp.Marked:
                    var marked = (MarkedBy)filter.Value!;
                    var markColumns = marked.Table.ColumnTypes();
                    sql.Append(column).Append(" IN (SELECT ").Append(dialect.Quote(Require(markColumns, "ref_docname"))).Append(" FROM ")
                        .Append(dialect.TableName(marked.Table.Name)).Append(" WHERE ").Append(dialect.Quote(Require(markColumns, "ref_doctype"))).Append(" = ")
                        .Append(dialect.Parameter(parameters.Count)).Append(" AND ").Append(dialect.Quote(Require(markColumns, marked.Field))).Append(" = ")
                        .Append(dialect.Parameter(parameters.Count + 1)).Append(')');
                    parameters.Add(marked.RefDocType);
                    parameters.Add(marked.Value);
                    break;
                case FilterOp.OwnerOrNames:
                    var shared = (OwnerOrNames)filter.Value!;
                    sql.Append('(').Append(column).Append(" = ").Append(dialect.Parameter(parameters.Count));
                    parameters.Add(shared.Owner);
                    if (shared.Names.Count > 0)
                    {
                        sql.Append(" OR ").Append(dialect.Quote("name")).Append(" IN (");
                        for (var n = 0; n < shared.Names.Count; n++)
                        {
                            sql.Append(n == 0 ? string.Empty : ", ").Append(dialect.Parameter(parameters.Count));
                            parameters.Add(shared.Names[n]);
                        }

                        sql.Append(')');
                    }

                    sql.Append(')');
                    break;
                case FilterOp.InOrNull:
                    var allowed = (filter.Value as IEnumerable<object?> ?? []).Where(v => v is not null).ToList();
                    sql.Append('(').Append(column).Append(" IS NULL");
                    if (allowed.Count > 0)
                    {
                        sql.Append(" OR ").Append(column).Append(" IN (");
                        for (var n = 0; n < allowed.Count; n++)
                        {
                            sql.Append(n == 0 ? string.Empty : ", ").Append(dialect.Parameter(parameters.Count));
                            parameters.Add(allowed[n]);
                        }

                        sql.Append(')');
                    }

                    sql.Append(')');
                    break;
                case FilterOp.NotIn:
                    var excluded = (filter.Value as IEnumerable<object?> ?? []).Where(v => v is not null).ToList();
                    if (excluded.Count == 0)
                    {
                        sql.Append("1 = 1");
                        break;
                    }

                    sql.Append('(').Append(column).Append(" IS NULL OR ").Append(column).Append(" NOT IN (");
                    for (var n = 0; n < excluded.Count; n++)
                    {
                        sql.Append(n == 0 ? string.Empty : ", ").Append(dialect.Parameter(parameters.Count));
                        parameters.Add(excluded[n]);
                    }

                    sql.Append("))");
                    break;
                case FilterOp.NotLike:
                    sql.Append('(').Append(column).Append(" IS NULL OR ").Append(column).Append(" NOT ").Append(dialect.LikeOperator).Append(' ')
                        .Append(dialect.Parameter(parameters.Count)).Append(dialect.LikeEscape).Append(')');
                    parameters.Add(filter.Value);
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
                    if (filter.Op == FilterOp.Like)
                        sql.Append(dialect.LikeEscape);
                    parameters.Add(filter.Value);
                    break;
            }
        }
    }

    private void CheckParameterCount(int count)
    {
        if (count > dialect.MaxParameters)
            throw new FormatException($"A filter may name at most {dialect.MaxParameters} values.");
    }

    private string Symbol(FilterOp op) => op switch
    {
        FilterOp.Eq => "=",
        FilterOp.Ne => "<>",
        FilterOp.Gt => ">",
        FilterOp.Lt => "<",
        FilterOp.Ge => ">=",
        FilterOp.Le => "<=",
        FilterOp.Like => dialect.LikeOperator,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Unsupported operator."),
    };

    private static string Require(IReadOnlyDictionary<string, FieldType> columns, string name) =>
        columns.ContainsKey(name) ? name : throw new ArgumentException($"'{name}' is not a column of this DocType.");
}
