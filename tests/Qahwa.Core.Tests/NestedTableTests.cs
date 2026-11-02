using Qahwa.Core.Designer;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>A table inside the rows of a table: an assembly has lines and each line has parts (ADR-147).</summary>
public class NestedTableTests
{
    private const string Part = """
        { "name": "Assembly Part", "isChildTable": true,
          "fields": [
            { "name": "part_name", "type": "Data", "required": true },
            { "name": "qty", "type": "Int", "required": true },
            { "name": "weight", "type": "Float", "formula": "qty * 2" },
            { "name": "line_title", "type": "Data", "formula": "parent.title" }
          ],
          "validations": [ { "expression": "qty <= 100", "message": "At most 100 of a part.", "field": "qty" } ] }
        """;

    private const string Line = """
        { "name": "Assembly Line", "isChildTable": true,
          "fields": [
            { "name": "title", "type": "Data", "required": true },
            { "name": "parts", "type": "Table", "options": "Assembly Part" },
            { "name": "part_count", "type": "Int", "formula": "sum(parts, 'qty')" }
          ] }
        """;

    private const string Assembly = """
        { "name": "Assembly", "autoname": "ASM-.####", "titleField": "name_text", "allowRename": true,
          "fields": [
            { "name": "name_text", "type": "Data", "required": true },
            { "name": "lines", "type": "Table", "options": "Assembly Line", "required": true },
            { "name": "total_parts", "type": "Int", "formula": "sum(lines, 'part_count')" }
          ],
          "permissions": [ { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true } ] }
        """;

    private readonly TestEngine _engine = new();
    private readonly DeletedDocumentService _deleted;

    public NestedTableTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse(Part));
        _engine.Registry.Add(DocTypeLoader.Parse(Line));
        _engine.Registry.Add(DocTypeLoader.Parse(Assembly));
        _engine.Registry.ValidateReferences();
        _deleted = new DeletedDocumentService(_engine.Registry, _engine.Repository, _engine.Permissions, _engine.History, _engine.Clock);
    }

    private static Dictionary<string, object?> P(string name, int qty) => Values(("part_name", name), ("qty", qty));

    private static Dictionary<string, object?> L(string title, params Dictionary<string, object?>[] parts) => Values(("title", title), ("parts", parts));

    private Task<Dictionary<string, object?>> CreateAsync(params Dictionary<string, object?>[] lines) =>
        _engine.Documents.InsertAsync("Assembly", Values(("name_text", "Frame"), ("lines", lines)), TestUsers.Manager);

    private static List<Dictionary<string, object?>> Lines(Dictionary<string, object?> doc) => (List<Dictionary<string, object?>>)doc["lines"]!;

    private static List<Dictionary<string, object?>> Parts(Dictionary<string, object?> line) => (List<Dictionary<string, object?>>)line["parts"]!;

    private async Task<long> CountAsync(string doctype) =>
        await _engine.Repository.CountAsync(_engine.Registry.Get(doctype), new ListQuery());

    // ---- filtering a list by rows inside rows ----

    private async Task<string[]> NamesAsync(params (string Field, string Op, object? Value)[] filters) =>
        (await _engine.Documents.ListAsync("Assembly", new ListRequest
        {
            Filters = filters.Select(f => new RawFilter(f.Field, f.Op, f.Value)).ToList(), Fields = ["name_text"], OrderBy = "name_text", Descending = false, Limit = 100,
        }, TestUsers.Manager)).Data.Select(r => (string)r["name_text"]!).ToArray();

    private Task AddAssemblyAsync(string name, params Dictionary<string, object?>[] lines) =>
        _engine.Documents.InsertAsync("Assembly", Values(("name_text", name), ("lines", lines)), TestUsers.Manager);

    [Fact]
    public async Task A_list_can_be_filtered_by_a_field_of_the_rows_inside_a_row()
    {
        await AddAssemblyAsync("A", L("Side", P("bolt", 4)), L("Top", P("panel", 1)));
        await AddAssemblyAsync("B", L("Side", P("nut", 9)));
        await AddAssemblyAsync("C", L("Base"));

        Assert.Equal(["A"], await NamesAsync(("lines.parts.part_name", "=", "bolt")));
        Assert.Equal(["B"], await NamesAsync(("lines.parts.qty", ">", 5)));
        Assert.Equal(["A", "B"], await NamesAsync(("lines.parts.qty", ">", 0)));
        Assert.Empty(await NamesAsync(("lines.parts.part_name", "=", "washer")));
    }

    [Fact]
    public async Task Filters_on_a_line_and_on_its_parts_must_hold_for_the_same_line_and_the_same_part()
    {
        await AddAssemblyAsync("A", L("Side", P("bolt", 4)), L("Top", P("nut", 9)));
        await AddAssemblyAsync("B", L("Top", P("bolt", 4)));

        // A has a Side line and (on another line) a nut of 9; no single line has both.
        Assert.Equal(["A"], await NamesAsync(("lines.title", "=", "Side"), ("lines.parts.part_name", "=", "bolt")));
        Assert.Empty(await NamesAsync(("lines.title", "=", "Side"), ("lines.parts.part_name", "=", "nut")));
        // The same part must satisfy both conditions on parts: a bolt of 4 (B), not a bolt and, elsewhere, a big nut (A).
        Assert.Equal(["A", "B"], await NamesAsync(("lines.parts.part_name", "=", "bolt"), ("lines.parts.qty", "=", 4)));
        Assert.Empty(await NamesAsync(("lines.parts.part_name", "=", "bolt"), ("lines.parts.qty", "=", 9)));
    }

    [Fact]
    public async Task A_filter_on_a_field_that_does_not_exist_inside_the_rows_is_refused()
    {
        await AddAssemblyAsync("A", L("Side", P("bolt", 4)));

        await Assert.ThrowsAsync<DocumentValidationException>(() => NamesAsync(("lines.parts.nothing", "=", "x")));
    }

    // ---- saving and reading ----

    [Fact]
    public async Task Rows_inside_rows_are_saved_and_read_back_in_order()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 4)), L("Top", P("panel", 1)));

        var lines = Lines(doc);
        Assert.Equal(["Side", "Top"], lines.Select(l => (string)l["title"]!));
        Assert.Equal(["bolt", "nut"], Parts(lines[0]).Select(p => (string)p["part_name"]!));
        Assert.Equal(["panel"], Parts(lines[1]).Select(p => (string)p["part_name"]!));
        Assert.Equal([1, 2], Parts(lines[0]).Select(p => (int)p["idx"]!));
        Assert.Equal(3, await CountAsync("Assembly Part"));

        // A nested row names the row that holds it, not the document.
        var stored = (await _engine.Repository.GetChildrenAsync(_engine.Registry.Get("Assembly Part"), "Assembly Line", (string)lines[0]["name"]!, "parts")).ToList();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, r => Assert.Equal("Assembly Line", r["parenttype"]));
    }

    [Fact]
    public async Task Formulas_work_at_every_level_and_a_nested_row_can_read_the_row_that_holds_it()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 6)), L("Top", P("panel", 1)));

        var lines = Lines(doc);
        Assert.Equal(10, lines[0]["part_count"]);
        Assert.Equal(1, lines[1]["part_count"]);
        Assert.Equal(11, doc["total_parts"]);
        Assert.Equal([8d, 12d], Parts(lines[0]).Select(p => (double)p["weight"]!));
        Assert.Equal(["Side", "Side"], Parts(lines[0]).Select(p => (string)p["line_title"]!)); // `parent` of a nested row is its line
    }

    [Fact]
    public async Task Editing_adding_and_removing_nested_rows_changes_only_what_differs()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 4)));
        var line = Lines(doc)[0];
        var bolt = Parts(line)[0];
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Assembly", (string)doc["name"]!, Values(("lines", new[]
        {
            Values(("name", line["name"]), ("parts", new[]
            {
                Values(("name", bolt["name"]), ("qty", 10)), // changed
                P("washer", 2),                              // new; the nut is not listed, so it goes
            })),
        })), TestUsers.Manager);

        var parts = Parts(Lines(updated)[0]);
        Assert.Equal(["bolt", "washer"], parts.Select(p => (string)p["part_name"]!));
        Assert.Equal(bolt["name"], parts[0]["name"]); // the same row, edited
        Assert.Equal(10, parts[0]["qty"]);
        Assert.Equal(12, Lines(updated)[0]["part_count"]);
        Assert.Equal(12, updated["total_parts"]);
        Assert.Equal(2, await CountAsync("Assembly Part")); // the nut is really gone
    }

    [Fact]
    public async Task A_saved_row_sent_without_its_nested_table_keeps_it_and_its_total()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 6)));
        var line = Lines(doc)[0];
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Assembly", (string)doc["name"]!, Values(("lines", new[]
        {
            Values(("name", line["name"]), ("title", "Side panel")),
        })), TestUsers.Manager);

        var kept = Lines(updated)[0];
        Assert.Equal("Side panel", kept["title"]);
        Assert.Equal(2, Parts(kept).Count);
        Assert.Equal(10, kept["part_count"]); // the formula still sees the parts the client did not send
        Assert.Equal(10, updated["total_parts"]);
    }

    [Fact]
    public async Task Removing_a_row_removes_the_rows_inside_it()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4)), L("Top", P("panel", 1), P("clip", 2)));
        var top = Lines(doc)[1];
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await _engine.Documents.UpdateAsync("Assembly", (string)doc["name"]!, Values(("lines", new[] { Values(("name", Lines(doc)[0]["name"])) })), TestUsers.Manager);

        Assert.Equal(1, await CountAsync("Assembly Part")); // only the bolt: the top's two parts went with it
        Assert.Empty(await _engine.Repository.GetChildrenAsync(_engine.Registry.Get("Assembly Part"), "Assembly Line", (string)top["name"]!, "parts"));
    }

    [Fact]
    public async Task Deleting_the_document_removes_every_row_at_every_level()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 4)), L("Top", P("panel", 1)));
        var other = await CreateAsync(L("Keep", P("keep me", 1)));

        await _engine.Documents.DeleteAsync("Assembly", (string)doc["name"]!, TestUsers.Manager);

        Assert.Equal(1, await CountAsync("Assembly Line"));
        Assert.Equal(["keep me"], (await _engine.Repository.ListAsync(_engine.Registry.Get("Assembly Part"), new ListQuery { Fields = ["part_name"] })).Select(r => (string)r["part_name"]!));
        Assert.NotNull(await _engine.Documents.GetAsync("Assembly", (string)other["name"]!, TestUsers.Manager));
    }

    [Fact]
    public async Task Renaming_a_document_keeps_its_nested_rows()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4)));

        await _engine.Documents.RenameAsync("Assembly", (string)doc["name"]!, "ASM-RENAMED", false, TestUsers.Manager);

        var renamed = await _engine.Documents.GetAsync("Assembly", "ASM-RENAMED", TestUsers.Manager);
        Assert.Equal(["bolt"], Parts(Lines(renamed)[0]).Select(p => (string)p["part_name"]!));
    }

    // ---- checks ----

    [Fact]
    public async Task Problems_in_a_nested_row_are_reported_with_the_whole_path()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            CreateAsync(L("Side", P("ok", 1), Values(("part_name", "no qty")))));
        Assert.Contains("lines[0].parts[1].qty", ex.Errors.Select(e => e.Field)); // required

        // The DocType's own rules run once nothing more basic is wrong.
        var rule = await Assert.ThrowsAsync<DocumentValidationException>(() => CreateAsync(L("Side", P("ok", 1), P("many", 500))));
        Assert.Contains("lines[0].parts[1].qty", rule.Errors.Select(e => e.Field)); // at most 100
        Assert.Contains("At most 100", rule.Errors[0].Message);
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Assembly"), new ListQuery()));
    }

    [Fact]
    public async Task A_nested_row_can_only_name_a_row_its_own_row_holds()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4)), L("Top", P("panel", 1)));
        var panel = Parts(Lines(doc)[1])[0];
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        // The panel belongs to the Top line; naming it inside the Side line must not move or edit it.
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Assembly", (string)doc["name"]!, Values(("lines", new[]
        {
            Values(("name", Lines(doc)[0]["name"]), ("parts", new[] { Values(("name", panel["name"]), ("qty", 9)) })),
            Values(("name", Lines(doc)[1]["name"])),
        })), TestUsers.Manager));

        Assert.Contains(ex.Errors, e => e.Field == "lines[0].parts[0].name");
        var unchanged = await _engine.Documents.GetAsync("Assembly", (string)doc["name"]!, TestUsers.Manager);
        Assert.Equal(1, Parts(Lines(unchanged)[1])[0]["qty"]);
    }

    [Fact]
    public async Task A_nested_table_that_is_required_needs_a_row_in_every_new_row()
    {
        var strict = DocTypeLoader.Parse(Line.Replace("\"name\": \"parts\", \"type\": \"Table\"", "\"name\": \"parts\", \"required\": true, \"type\": \"Table\""));
        _engine.Registry.Upsert(strict, DocTypeSource.Custom);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => CreateAsync(L("Empty")));

        Assert.Contains(ex.Errors, e => e.Field == "lines[0].parts");
        await CreateAsync(L("Full", P("bolt", 1)));
    }

    [Fact]
    public async Task One_save_cannot_carry_an_unreasonable_number_of_rows()
    {
        var lines = Enumerable.Range(0, 11).Select(i => L($"Line {i}", Enumerable.Range(0, 1000).Select(j => P($"p{j}", 1)).ToArray())).ToArray();

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => CreateAsync(lines));

        Assert.Contains("rows in all its tables", ex.Errors[0].Message);
    }

    // ---- the DocType rules ----

    [Fact]
    public void Tables_nest_one_level_deep_only()
    {
        var registry = new MetaRegistry();
        registry.Add(DocTypeLoader.Parse(Part));
        registry.Add(DocTypeLoader.Parse(Line));

        var deeper = DocTypeLoader.Parse("""
            { "name": "Assembly Group", "isChildTable": true, "fields": [ { "name": "lines", "type": "Table", "options": "Assembly Line" } ] }
            """);
        Assert.Contains("fields[0].options", registry.CheckReferences(deeper).Select(e => e.Field)); // a line holds a table, so a group cannot hold lines

        var self = DocTypeLoader.Parse("""
            { "name": "Loop", "isChildTable": true, "fields": [ { "name": "again", "type": "Table", "options": "Loop" } ] }
            """);
        Assert.Contains("fields[0].options", registry.CheckReferences(self).Select(e => e.Field)); // not even a table of itself
    }

    [Fact]
    public void A_child_table_used_inside_another_child_cannot_start_holding_a_table()
    {
        // Assembly Part is the rows of a table inside Assembly Line; it cannot itself get a table.
        var grown = DocTypeLoader.Parse(Part) with
        {
            Fields = [.. DocTypeLoader.Parse(Part).Fields, new DocFieldMeta { Name = "notes", Type = Abstractions.FieldType.Table, Options = "Assembly Part" }],
        };

        var errors = _engine.Registry.CheckReferences(grown).Select(e => e.Message).ToList();

        Assert.Contains(errors, m => m.Contains("used as a table inside 'Assembly Line'"));
    }

    // ---- what rides along ----

    [Fact]
    public async Task The_history_records_a_change_inside_a_nested_table()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4)));
        var line = Lines(doc)[0];
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Assembly", (string)doc["name"]!, Values(("lines", new[]
        {
            Values(("name", line["name"]), ("parts", new[] { Values(("name", Parts(line)[0]["name"]), ("qty", 7)), P("nut", 1) })),
        })), TestUsers.Manager);

        var timeline = await _engine.Activity.TimelineAsync("Assembly", (string)doc["name"]!, TestUsers.Manager);

        var update = timeline.Single(i => i.Kind == "version" && i.Event == "Update");
        var changed = update.Data!["tables"]!.AsObject()["lines › parts"]!.AsObject(); // a table inside a row is logged as "table › inner table"
        Assert.Single(changed["added"]!.AsArray());   // the nut
        Assert.Single(changed["changed"]!.AsArray()); // the bolt's quantity
    }

    [Fact]
    public async Task Copies_of_a_document_carry_the_nested_rows_without_their_ids()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 4)));
        var source = await _engine.Documents.GetAsync("Assembly", (string)doc["name"]!, TestUsers.Manager);
        var child = _engine.Registry.Get("Assembly Line");

        var copy = DocumentService.CopyRows(_engine.Registry, child, Lines(source), f => f.Type != Abstractions.FieldType.Password && f.Formula is null);

        var line = Assert.Single(copy);
        Assert.False(line.ContainsKey("name"));
        var parts = (List<Dictionary<string, object?>>)line["parts"]!;
        Assert.Equal(["bolt", "nut"], parts.Select(p => (string)p["part_name"]!));
        Assert.All(parts, p => Assert.False(p.ContainsKey("name")));
    }

    [Fact]
    public async Task A_deleted_document_comes_back_with_its_nested_rows()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4), P("nut", 2)), L("Top", P("panel", 1)));
        var name = (string)doc["name"]!;
        await _engine.Documents.DeleteAsync("Assembly", name, TestUsers.Manager);
        Assert.Equal(0, await CountAsync("Assembly Part"));
        var kept = (await _engine.Documents.ListAsync("Deleted Document", new ListRequest { Fields = ["name"], Limit = 5 }, TestUsers.Admin)).Data.Single();

        await _deleted.RestoreAsync((string)kept["name"]!, TestUsers.Admin);

        var restored = await _engine.Documents.GetAsync("Assembly", name, TestUsers.Manager);
        Assert.Equal(["bolt", "nut"], Parts(Lines(restored)[0]).Select(p => (string)p["part_name"]!));
        Assert.Equal(["panel"], Parts(Lines(restored)[1]).Select(p => (string)p["part_name"]!));
        Assert.Equal(7, restored["total_parts"]);
    }

    [Fact]
    public async Task The_default_print_shows_nested_rows_under_their_row()
    {
        var doc = await CreateAsync(L("Side", P("bolt", 4)));

        var page = await _engine.Print.RenderAsync("Assembly", (string)doc["name"]!, null, null, TestUsers.Manager);

        Assert.Contains("Side", page.Html);
        Assert.Contains("bolt", page.Html);
    }
}
