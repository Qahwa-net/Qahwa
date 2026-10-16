using Qahwa.Abstractions;
using Qahwa.Data.SqlServer;

namespace Qahwa.Data.Tests;

public class SqlServerDialectTests
{
    private readonly ISqlDialect _d = new SqlServerDialect();

    [Fact]
    public void Identifiers_are_quoted_with_brackets_and_a_closing_bracket_cannot_escape()
    {
        Assert.Equal("[tabCustomer]", _d.Quote("tabCustomer"));
        Assert.Equal("[a]]; DROP TABLE x; --]", _d.Quote("a]; DROP TABLE x; --"));
        Assert.Equal("[tabSales Order]", _d.TableName("Sales Order"));
    }

    [Theory]
    [InlineData(FieldType.Data, "nvarchar(140)")]
    [InlineData(FieldType.Link, "nvarchar(140)")]
    [InlineData(FieldType.Text, "nvarchar(max)")]
    [InlineData(FieldType.Int, "int")]
    [InlineData(FieldType.Float, "float")]
    [InlineData(FieldType.Currency, "decimal(21,9)")]
    [InlineData(FieldType.Check, "bit")]
    [InlineData(FieldType.Date, "date")]
    [InlineData(FieldType.Datetime, "datetime2(6)")]
    [InlineData(FieldType.Time, "time(6)")]
    [InlineData(FieldType.Rating, "int")]
    [InlineData(FieldType.Json, "nvarchar(max)")]
    public void Field_types_map_to_columns(FieldType type, string expected)
    {
        Assert.Equal(expected, _d.ColumnType(type));
        Assert.Equal(expected, _d.CatalogType(type)); // the inspector reports the same text
    }

    [Fact]
    public void Fields_without_a_column_are_refused() => Assert.Throws<InvalidOperationException>(() => _d.ColumnType(FieldType.SectionBreak));

    [Fact]
    public void Paging_uses_offset_fetch_with_whole_numbers_only()
    {
        Assert.Equal("OFFSET 40 ROWS FETCH NEXT 20 ROWS ONLY", _d.Page(20, 40));
        Assert.Equal("OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY", _d.Page(-5, -9));
    }

    [Fact]
    public void Indexes_and_tables_are_created_only_when_missing_and_unique_ones_ignore_empty_values()
    {
        var plain = _d.CreateIndexSql("ix_1", "[tabCustomer]", "region", unique: false);
        var unique = _d.CreateIndexSql("ux_1", "[tabCustomer]", "email", unique: true);

        Assert.StartsWith("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'ix_1'", plain);
        Assert.EndsWith("CREATE INDEX [ix_1] ON [tabCustomer] ([region])", plain);
        Assert.Contains("CREATE UNIQUE INDEX [ux_1] ON [tabCustomer] ([email]) WHERE [email] IS NOT NULL", unique);
        Assert.Equal("DROP INDEX IF EXISTS [ux_1] ON [tabCustomer]", _d.DropIndexSql("ux_1", "[tabCustomer]"));
        Assert.Equal("ALTER TABLE [tabCustomer] ADD [since] date", _d.AddColumnSql("[tabCustomer]", "[since] date"));
        Assert.StartsWith("IF OBJECT_ID(N'__auth', N'U') IS NULL CREATE TABLE [__auth]", _d.CreateAuthTableSql);
    }

    [Fact]
    public void Names_in_literals_cannot_break_out_of_them()
    {
        var sql = _d.CreateIndexSql("ix_'; DROP TABLE x; --", "[tab'x]", "c", unique: false);

        Assert.Contains("N'ix_''; DROP TABLE x; --'", sql);
        Assert.Contains("OBJECT_ID(N'[tab''x]')", sql);
    }

    [Fact]
    public void The_series_counter_is_one_locked_step_and_every_value_is_a_parameter()
    {
        Assert.Contains("UPDLOCK, HOLDLOCK", _d.NextSeriesSql);
        Assert.Contains("@p0", _d.NextSeriesSql);
        Assert.DoesNotContain("'", _d.NextSeriesSql);
        Assert.Contains("@p1", _d.SetSeriesSql);
    }

    [Fact]
    public void Parameters_are_named_by_position() => Assert.Equal("@p7", _d.Parameter(7));
}
