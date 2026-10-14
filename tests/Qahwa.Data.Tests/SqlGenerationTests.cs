using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Data.Postgres;
using Qahwa.Testing;

namespace Qahwa.Data.Tests;

/// <summary>SQL generation tests: no database or Docker needed.</summary>
public class SqlGenerationTests
{
    private static readonly PostgresDialect Dialect = new();
    private static readonly DocTypeMeta Customer = TestMetas.Registry().Get("Customer");

    [Fact]
    public void Planner_creates_a_new_table_with_standard_columns_and_indexes()
    {
        var plan = new SchemaPlanner(Dialect).Plan(Customer, null);

        Assert.False(plan.TableExists);
        var create = plan.Statements[0].Sql;
        Assert.StartsWith("CREATE TABLE \"tabCustomer\"", create);
        Assert.Contains("\"name\" varchar(140) NOT NULL PRIMARY KEY", create);
        Assert.Contains("\"docstatus\" integer NOT NULL DEFAULT 0", create);
        Assert.Contains("\"credit_limit\" numeric(21,9)", create);
        Assert.Contains("\"since\" date", create);
        Assert.Contains("\"active\" boolean", create);
        Assert.Contains(plan.Statements, s => s.Sql.StartsWith("CREATE UNIQUE INDEX IF NOT EXISTS"));
        Assert.Contains(plan.Statements, s => s.Description.Contains("modified"));
    }

    [Fact]
    public void Planner_only_adds_missing_columns_and_never_alters_types()
    {
        var current = new TableSchema(
            new Dictionary<string, string>
            {
                ["name"] = "character varying", ["creation"] = "timestamp with time zone",
                ["modified"] = "timestamp with time zone", ["modified_by"] = "character varying",
                ["owner"] = "character varying", ["docstatus"] = "integer", ["idx"] = "integer",
                ["customer_name"] = "character varying", ["customer_type"] = "character varying",
                ["territory"] = "character varying", ["since"] = "date", ["active"] = "boolean",
                ["credit_limit"] = "text", // wrong on purpose
            },
            new HashSet<string>
            {
                SchemaPlanner.IndexName("tabCustomer", "modified", unique: false),
                SchemaPlanner.IndexName("tabCustomer", "customer_name", unique: true),
            });

        var plan = new SchemaPlanner(Dialect).Plan(Customer, current);

        Assert.True(plan.IsEmpty);
        Assert.Single(plan.Warnings);
        Assert.Contains("credit_limit", plan.Warnings[0]);
    }

    [Fact]
    public void Planner_adds_a_new_field_with_alter_table()
    {
        var current = new TableSchema(
            Customer.ColumnTypes().Where(c => c.Key != "since")
                .ToDictionary(c => c.Key, c => Dialect.CatalogType(c.Value)),
            new HashSet<string>
            {
                SchemaPlanner.IndexName("tabCustomer", "modified", unique: false),
                SchemaPlanner.IndexName("tabCustomer", "customer_name", unique: true),
            });

        var plan = new SchemaPlanner(Dialect).Plan(Customer, current);

        var statement = Assert.Single(plan.Statements);
        Assert.Equal("ALTER TABLE \"tabCustomer\" ADD COLUMN \"since\" date", statement.Sql);
    }

    [Fact]
    public void Planner_drops_the_unique_index_of_a_field_that_is_no_longer_unique_but_never_the_column()
    {
        var meta = Customer with { Fields = Customer.Fields.Select(f => f with { Unique = false }).ToList() };
        var current = new TableSchema(
            Customer.ColumnTypes().Concat(new Dictionary<string, FieldType> { ["orphan"] = FieldType.Data })
                .ToDictionary(c => c.Key, c => Dialect.CatalogType(c.Value)),
            new HashSet<string>
            {
                SchemaPlanner.IndexName("tabCustomer", "modified", unique: false),
                SchemaPlanner.IndexName("tabCustomer", "customer_name", unique: true),
                SchemaPlanner.IndexName("tabCustomer", "orphan", unique: true),
            });

        var plan = new SchemaPlanner(Dialect).Plan(meta, current);

        Assert.Equal(2, plan.Statements.Count(s => s.Sql.StartsWith("DROP INDEX IF EXISTS")));
        Assert.DoesNotContain(plan.Statements, s => s.Sql.Contains("DROP COLUMN") || s.Sql.StartsWith("DROP TABLE"));
    }

    [Fact]
    public void Select_binds_values_as_parameters_and_never_inlines_them()
    {
        const string hostile = "x'; DROP TABLE \"tabCustomer\"; --";
        var statement = new SqlBuilder(Dialect).Select(Customer, new ListQuery
        {
            Filters =
            [
                new QueryFilter("customer_name", FilterOp.Eq, hostile),
                new QueryFilter("customer_type", FilterOp.In, new List<object?> { "Company", "Individual" }),
                new QueryFilter("credit_limit", FilterOp.IsNotNull),
            ],
            Fields = ["name", "customer_name"],
            OrderBy = "customer_name",
            Descending = false,
            Limit = 10,
            Offset = 20,
        });

        Assert.DoesNotContain("DROP", statement.Sql);
        Assert.Equal(
            "SELECT \"name\", \"customer_name\" FROM \"tabCustomer\" " +
            "WHERE \"customer_name\" = @p0 AND \"customer_type\" IN (@p1, @p2) AND \"credit_limit\" IS NOT NULL " +
            "ORDER BY \"customer_name\" ASC, \"name\" ASC LIMIT 10 OFFSET 20",
            statement.Sql);
        Assert.Equal<object?>([hostile, "Company", "Individual"], statement.Parameters);
    }

    [Fact]
    public void Builder_rejects_identifiers_that_are_not_columns()
    {
        var builder = new SqlBuilder(Dialect);

        Assert.Throws<ArgumentException>(() => builder.Select(Customer, new ListQuery { Fields = ["name\" FROM x; --"] }));
        Assert.Throws<ArgumentException>(() => builder.Select(Customer, new ListQuery { OrderBy = "nope" }));
        Assert.Throws<ArgumentException>(() =>
            builder.Insert(Customer, new Dictionary<string, object?> { ["nope"] = 1 }));
        Assert.Throws<ArgumentException>(() =>
            builder.Update(Customer, "x", new Dictionary<string, object?> { ["name"] = "renamed" }, null));
    }

    [Fact]
    public void Empty_in_list_matches_nothing()
    {
        var statement = new SqlBuilder(Dialect).Count(Customer, new ListQuery
        {
            Filters = [new QueryFilter("customer_type", FilterOp.In, new List<object?>())],
        });

        Assert.Contains("1 = 0", statement.Sql);
    }

    [Fact]
    public void Update_adds_concurrency_condition_when_requested()
    {
        var statement = new SqlBuilder(Dialect).Update(
            Customer, "CUST-00001",
            new Dictionary<string, object?> { ["credit_limit"] = 5m },
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(
            "UPDATE \"tabCustomer\" SET \"credit_limit\" = @p0 WHERE \"name\" = @p1 AND \"modified\" = @p2",
            statement.Sql);
    }

    [Theory]
    [InlineData(FieldType.Data, "varchar(140)", "character varying")]
    [InlineData(FieldType.Link, "varchar(140)", "character varying")]
    [InlineData(FieldType.LongText, "text", "text")]
    [InlineData(FieldType.Currency, "numeric(21,9)", "numeric")]
    [InlineData(FieldType.Datetime, "timestamp with time zone", "timestamp with time zone")]
    public void Dialect_maps_types(FieldType type, string ddl, string catalog)
    {
        Assert.Equal(ddl, Dialect.ColumnType(type));
        Assert.Equal(catalog, Dialect.CatalogType(type));
    }

    [Fact]
    public void Quote_escapes_embedded_quotes() =>
        Assert.Equal("\"a\"\"b\"", Dialect.Quote("a\"b"));
}
