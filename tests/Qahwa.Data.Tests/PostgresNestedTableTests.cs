using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>Tables inside the rows of a table against real PostgreSQL: the cascades are SQL (ADR-147).</summary>
public class PostgresNestedTableTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static Dictionary<string, object?> P(string name, int qty) => Values(("part_name", name), ("qty", qty));

    private static Dictionary<string, object?> L(string title, params Dictionary<string, object?>[] parts) => Values(("title", title), ("parts", parts));

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Assembly Part", "isChildTable": true, "fields": [ { "name": "part_name", "type": "Data", "required": true }, { "name": "qty", "type": "Int" } ] }
            """));
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Assembly Line", "isChildTable": true, "fields": [ { "name": "title", "type": "Data" }, { "name": "parts", "type": "Table", "options": "Assembly Part" },
              { "name": "part_count", "type": "Int", "formula": "sum(parts, 'qty')" } ] }
            """));
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Assembly", "autoname": "ASM-.####", "allowRename": true, "fields": [ { "name": "label", "type": "Data" }, { "name": "lines", "type": "Table", "options": "Assembly Line" },
              { "name": "total", "type": "Int", "formula": "sum(lines, 'part_count')" } ] }
            """));
        db.Registry.ValidateReferences();
        await db.MigrateAsync();
        return db;
    }

    private static List<Dictionary<string, object?>> Lines(Dictionary<string, object?> doc) => (List<Dictionary<string, object?>>)doc["lines"]!;

    private static List<Dictionary<string, object?>> Parts(Dictionary<string, object?> line) => (List<Dictionary<string, object?>>)line["parts"]!;

    [SkippableFact]
    public async Task Nested_rows_are_saved_edited_removed_and_deleted_with_their_rows_on_postgres()
    {
        await using var db = await StackAsync();
        var doc = await db.Documents.InsertAsync("Assembly", Values(("label", "Frame"), ("lines", new[] { L("Side", P("bolt", 4), P("nut", 6)), L("Top", P("panel", 1)) })), TestUsers.Admin);
        var name = (string)doc["name"]!;
        Assert.Equal(11, doc["total"]);
        Assert.Equal(3, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Part\""));

        // Edit one part, drop another, add one; drop the Top line altogether.
        var side = Lines(doc)[0];
        db.Clock.Advance(TimeSpan.FromMinutes(1));
        var updated = await db.Documents.UpdateAsync("Assembly", name, Values(("lines", new[]
        {
            Values(("name", side["name"]), ("parts", new[] { Values(("name", Parts(side)[0]["name"]), ("qty", 10)), P("washer", 2) })),
        })), TestUsers.Admin);

        Assert.Equal(["bolt", "washer"], Parts(Lines(updated)[0]).Select(p => (string)p["part_name"]!));
        Assert.Equal(12, updated["total"]);
        Assert.Equal(2, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Part\""));   // nut and panel are gone
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Line\""));

        // Another document's rows must be left alone by every cascade.
        var other = await db.Documents.InsertAsync("Assembly", Values(("label", "Other"), ("lines", new[] { L("Keep", P("keep", 1)) })), TestUsers.Admin);
        await db.Documents.DeleteAsync("Assembly", name, TestUsers.Admin);

        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Part\""));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Line\""));
        Assert.Equal("keep", Parts(Lines(await db.Documents.GetAsync("Assembly", (string)other["name"]!, TestUsers.Admin))[0])[0]["part_name"]);
    }

    [SkippableFact]
    public async Task Renaming_and_merging_keep_or_remove_nested_rows_correctly_on_postgres()
    {
        await using var db = await StackAsync();
        var a = (string)(await db.Documents.InsertAsync("Assembly", Values(("label", "A"), ("lines", new[] { L("Side", P("bolt", 4)) })), TestUsers.Admin))["name"]!;
        var b = (string)(await db.Documents.InsertAsync("Assembly", Values(("label", "B"), ("lines", new[] { L("Top", P("panel", 1)) })), TestUsers.Admin))["name"]!;

        await db.Documents.RenameAsync("Assembly", a, "ASM-A2", false, TestUsers.Admin);
        Assert.Equal(["bolt"], Parts(Lines(await db.Documents.GetAsync("Assembly", "ASM-A2", TestUsers.Admin))[0]).Select(p => (string)p["part_name"]!));

        await db.Documents.RenameAsync("Assembly", b, "ASM-A2", true, TestUsers.Admin); // merge B into A2: B and everything under it goes
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Line\""));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabAssembly Part\""));
    }
}
