using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>Child tables against real PostgreSQL: DDL, row diffs, cascade and transactions (needs Docker).</summary>
public class PostgresChildTableTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly UserContext Admin = TestUsers.Admin;

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        var db = await postgres.CreateStackAsync();

        await db.Designer.CreateAsync(new DocTypeMeta
        {
            Name = "Order Line",
            IsChildTable = true,
            Fields =
            [
                new DocFieldMeta { Name = "item", Type = FieldType.Data, Required = true },
                new DocFieldMeta { Name = "qty", Type = FieldType.Int },
                new DocFieldMeta { Name = "rate", Type = FieldType.Currency },
                new DocFieldMeta { Name = "due", Type = FieldType.Date },
            ],
        }, Admin);
        await db.Designer.CreateAsync(new DocTypeMeta
        {
            Name = "Order",
            Autoname = "ORD-.####",
            Fields =
            [
                new DocFieldMeta { Name = "title", Type = FieldType.Data },
                new DocFieldMeta { Name = "lines", Type = FieldType.Table, Options = "Order Line" },
            ],
        }, Admin);
        return db;
    }

    private static Dictionary<string, object?> Line(string item, int qty, params (string, object?)[] more) =>
        Values([("item", item), ("qty", qty), .. more]);

    private static List<Dictionary<string, object?>> Lines(Dictionary<string, object?> doc) =>
        (List<Dictionary<string, object?>>)doc["lines"]!;

    [SkippableFact]
    public async Task The_child_table_has_the_parent_columns_and_an_index_and_the_parent_has_no_table_column()
    {
        await using var db = await StackAsync();

        Assert.Equal(1, await db.ColumnCountAsync("tabOrder Line", "parent", "parenttype", "parentfield", "idx") / 4);
        Assert.Equal(0, await db.ColumnCountAsync("tabOrder", "lines"));
        Assert.Equal(1, await db.IndexCountAsync("tabOrder Line", "parent"));
    }

    [SkippableFact]
    public async Task Rows_round_trip_with_types_and_order()
    {
        await using var db = await StackAsync();

        var doc = await db.Documents.InsertAsync("Order", Values(("title", "T"), ("lines", new[]
        {
            Line("Pen", 2, ("rate", "1.50"), ("due", "2026-03-09")),
            Line("Book", 5),
        })), Admin);

        var lines = Lines(doc);
        Assert.Equal(["Pen", "Book"], lines.Select(l => (string)l["item"]!));
        Assert.Equal(1.5m, lines[0]["rate"]);
        Assert.Equal(new DateOnly(2026, 3, 9), lines[0]["due"]);
        Assert.Equal([1, 2], lines.Select(l => (int)l["idx"]!));
        Assert.Equal(2, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabOrder Line\" WHERE parenttype = 'Order' AND parentfield = 'lines'"));
    }

    [SkippableFact]
    public async Task Update_applies_adds_changes_reorders_and_deletes_in_one_go()
    {
        await using var db = await StackAsync();
        var doc = await db.Documents.InsertAsync("Order", Values(("title", "T"), ("lines", new[] { Line("A", 1), Line("B", 2), Line("C", 3) })), Admin);
        var rows = Lines(doc);
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await db.Documents.UpdateAsync("Order", "ORD-0001", Values(("lines", new[]
        {
            Values(("name", rows[2]["name"]), ("qty", 30)),
            Values(("name", rows[0]["name"])),
            Line("D", 4),
        })), Admin);

        Assert.Equal(["C", "A", "D"], Lines(updated).Select(l => (string)l["item"]!));
        Assert.Equal(30, Lines(updated)[0]["qty"]);
        Assert.Equal(3, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabOrder Line\""));
        Assert.Equal(rows[2]["name"], Lines(updated)[0]["name"]);
    }

    [SkippableFact]
    public async Task Deleting_the_parent_deletes_only_its_own_rows()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Order", Values(("title", "A"), ("lines", new[] { Line("A1", 1), Line("A2", 1) })), Admin);
        await db.Documents.InsertAsync("Order", Values(("title", "B"), ("lines", new[] { Line("B1", 1) })), Admin);

        await db.Documents.DeleteAsync("Order", "ORD-0001", Admin);

        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabOrder Line\""));
        Assert.Equal("B1", Lines(await db.Documents.GetAsync("Order", "ORD-0002", Admin))[0]["item"]);
    }

    [SkippableFact]
    public async Task A_failure_while_writing_rows_rolls_back_the_whole_document()
    {
        await using var db = await StackAsync();
        var parent = db.Registry.Get("Order");
        var child = db.Registry.Get("Order Line");
        var now = DateTime.UtcNow;
        IReadOnlyDictionary<string, object?> ChildRow(string name) => Values(
            ("name", name), ("parent", "X"), ("parenttype", "Order"), ("parentfield", "lines"), ("idx", 1), ("item", "x"),
            ("creation", now), ("modified", now), ("modified_by", "t"), ("owner", "t"), ("docstatus", 0));

        await Assert.ThrowsAsync<ConflictException>(() => db.Repository.InsertAsync(
            parent,
            Values(("name", "X"), ("title", "t"), ("creation", now), ("modified", now), ("modified_by", "t"), ("owner", "t"), ("docstatus", 0), ("idx", 0)),
            [new ChildChanges(child, [ChildRow("same"), ChildRow("same")], [], [])]));

        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabOrder\""));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabOrder Line\""));
    }

    [SkippableFact]
    public async Task A_stale_parent_version_rolls_back_the_row_changes_too()
    {
        await using var db = await StackAsync();
        var doc = await db.Documents.InsertAsync("Order", Values(("title", "T"), ("lines", new[] { Line("A", 1) })), Admin);
        db.Clock.Advance(TimeSpan.FromMinutes(1));
        await db.Documents.UpdateAsync("Order", "ORD-0001", Values(("title", "changed")), Admin);

        await Assert.ThrowsAsync<ConflictException>(() => db.Documents.UpdateAsync(
            "Order", "ORD-0001", Values(("lines", new[] { Line("Z", 9) }), ("modified", doc["modified"])), Admin));

        Assert.Equal("A", Lines(await db.Documents.GetAsync("Order", "ORD-0001", Admin))[0]["item"]);
    }

    [SkippableFact]
    public async Task Large_tables_work()
    {
        await using var db = await StackAsync();
        var rows = Enumerable.Range(1, 800).Select(i => Line($"Item {i}", i)).ToArray();

        var doc = await db.Documents.InsertAsync("Order", Values(("title", "Big"), ("lines", rows)), Admin);

        Assert.Equal(800, Lines(doc).Count);
        Assert.Equal("Item 800", Lines(doc)[^1]["item"]);
        Assert.Equal(Enumerable.Range(1, 800), Lines(doc).Select(l => (int)l["idx"]!));
    }

    [SkippableFact]
    public async Task Custom_child_tables_survive_a_restart()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Order", Values(("title", "T"), ("lines", new[] { Line("A", 1) })), Admin);

        var fresh = TestMetas.Registry();
        var failures = await new Core.Designer.CustomDocTypeLoader(db.DocTypeStore, fresh).LoadAsync();

        Assert.Empty(failures);
        Assert.True(fresh.Get("Order Line").IsChildTable);
        Assert.Equal("Order Line", fresh.Get("Order").Fields.Single(f => f.Type == FieldType.Table).Options);
        Assert.Empty(fresh.CheckReferences(fresh.Get("Order")));
    }
}
