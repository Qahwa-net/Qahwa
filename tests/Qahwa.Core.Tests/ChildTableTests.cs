using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ChildTableTests
{
    private const string OrderItem = """
        {
          "name": "Order Item", "isChildTable": true,
          "fields": [
            { "name": "item_name", "type": "Data", "required": true, "inListView": true },
            { "name": "qty", "type": "Int", "required": true },
            { "name": "rate", "type": "Currency" },
            { "name": "uom", "type": "Select", "options": "Nos\nKg", "default": "Nos" },
            { "name": "territory", "type": "Link", "options": "Territory" }
          ]
        }
        """;

    private const string OrderNote = """
        { "name": "Order Note", "isChildTable": true, "fields": [ { "name": "note", "type": "Text", "required": true } ] }
        """;

    private const string SalesOrder = """
        {
          "name": "Sales Order", "autoname": "SO-.####", "titleField": "title",
          "fields": [
            { "name": "title", "type": "Data", "required": true },
            { "name": "items", "type": "Table", "options": "Order Item", "required": true },
            { "name": "notes", "type": "Table", "options": "Order Note" }
          ],
          "permissions": [
            { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true },
            { "role": "Sales User", "read": true, "write": true, "create": true, "ifOwner": true }
          ]
        }
        """;

    private readonly TestEngine _engine = new();

    public ChildTableTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse(OrderItem));
        _engine.Registry.Add(DocTypeLoader.Parse(OrderNote));
        _engine.Registry.Add(DocTypeLoader.Parse(SalesOrder));
        _engine.Registry.ValidateReferences();
    }

    private static Dictionary<string, object?> Row(string item, int qty, params (string, object?)[] more) =>
        Values([("item_name", item), ("qty", qty), .. more]);

    private Task<Dictionary<string, object?>> CreateOrderAsync(IUserContext? user = null, params Dictionary<string, object?>[] rows) =>
        _engine.Documents.InsertAsync(
            "Sales Order", Values(("title", "Order A"), ("items", rows.Length > 0 ? rows : [Row("Pen", 2)])), user ?? TestUsers.Manager);

    private static List<Dictionary<string, object?>> Items(Dictionary<string, object?> doc) =>
        (List<Dictionary<string, object?>>)doc["items"]!;

    [Fact]
    public async Task Insert_saves_rows_in_order_with_defaults_and_ids()
    {
        var doc = await CreateOrderAsync(null, Row("Pen", 2, ("rate", 1.5m)), Row("Book", 5));

        Assert.Equal("SO-0001", doc["name"]);
        var items = Items(doc);
        Assert.Equal(["Pen", "Book"], items.Select(i => (string)i["item_name"]!));
        Assert.Equal([1, 2], items.Select(i => (int)i["idx"]!));
        Assert.Equal(1.5m, items[0]["rate"]);
        Assert.Equal("Nos", items[0]["uom"]);                     // default applied to the row
        Assert.All(items, i => Assert.Equal(10, ((string)i["name"]!).Length));
        Assert.DoesNotContain("parent", items[0].Keys);           // internal columns are not exposed
        Assert.Empty((List<Dictionary<string, object?>>)(await _engine.Documents.GetAsync("Sales Order", "SO-0001", TestUsers.Manager))["notes"]!);
    }

    [Fact]
    public async Task Rows_can_arrive_as_json_like_they_do_from_the_api()
    {
        var json = JsonDocument.Parse("""[ { "item_name": "Pen", "qty": "3" }, { "item_name": "Ink", "qty": 4, "uom": "Kg" } ]""").RootElement;

        var doc = await _engine.Documents.InsertAsync(
            "Sales Order", Values(("title", "T"), ("items", json)), TestUsers.Manager);

        Assert.Equal([3, 4], Items(doc).Select(i => (int)i["qty"]!));
        Assert.Equal("Kg", Items(doc)[1]["uom"]);
    }

    [Fact]
    public async Task Row_errors_are_reported_with_the_row_and_field()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            CreateOrderAsync(null,
                Row("Pen", 1),
                Values(("qty", "lots"), ("bogus", 1), ("uom", "Tonne")),
                Row("Book", 1, ("territory", "Nowhere"))));

        var paths = ex.Errors.Select(e => e.Field).ToHashSet();
        Assert.Contains("items[1].item_name", paths); // required
        Assert.Contains("items[1].qty", paths);       // not a number
        Assert.Contains("items[1].bogus", paths);     // unknown field
        Assert.Contains("items[1].uom", paths);       // not an option
        Assert.Contains("items[2].territory", paths); // link target missing
        Assert.DoesNotContain(paths, p => p.StartsWith("items[0]"));
    }

    [Fact]
    public async Task A_required_table_needs_at_least_one_row()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Order", Values(("title", "T")), TestUsers.Manager));
        Assert.Contains(ex.Errors, e => e.Field == "items");

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Order", Values(("title", "T"), ("items", new List<object>())), TestUsers.Manager));
    }

    [Fact]
    public async Task Malformed_tables_and_oversized_tables_are_refused()
    {
        var notAList = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Order", Values(("title", "T"), ("items", "nope")), TestUsers.Manager));
        Assert.Contains(notAList.Errors, e => e.Field == "items");

        var notObjects = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Order", Values(("title", "T"), ("items", new object[] { 1, 2 })), TestUsers.Manager));
        Assert.Contains(notObjects.Errors, e => e.Field == "items[0]");

        var tooMany = Enumerable.Range(0, DocumentService.MaxRows + 1).Select(i => Row("x", i)).ToArray();
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => CreateOrderAsync(null, tooMany));
        Assert.Contains(ex.Errors, e => e.Field == "items" && e.Message.Contains("at most"));
    }

    [Fact]
    public async Task Update_without_the_table_leaves_the_rows_alone()
    {
        var doc = await CreateOrderAsync(null, Row("Pen", 2), Row("Book", 5));
        var before = Items(doc).Select(i => (string)i["name"]!).ToList();

        var updated = await _engine.Documents.UpdateAsync("Sales Order", "SO-0001", Values(("title", "Renamed")), TestUsers.Manager);

        Assert.Equal("Renamed", updated["title"]);
        Assert.Equal(before, Items(updated).Select(i => (string)i["name"]!));
    }

    [Fact]
    public async Task Update_adds_changes_reorders_and_removes_rows_by_comparing_with_what_is_saved()
    {
        var doc = await CreateOrderAsync(null, Row("Pen", 2), Row("Book", 5), Row("Ink", 7));
        var (pen, book, ink) = (Items(doc)[0], Items(doc)[1], Items(doc)[2]);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Sales Order", "SO-0001", Values(("items", new[]
        {
            Values(("name", ink["name"]), ("qty", 70)),             // moved to the front and changed
            Values(("name", pen["name"])),                          // unchanged, moved to the middle
            Row("Stapler", 1),                                      // new
        })), TestUsers.Manager);

        var items = Items(updated);
        Assert.Equal(["Ink", "Pen", "Stapler"], items.Select(i => (string)i["item_name"]!));
        Assert.Equal([1, 2, 3], items.Select(i => (int)i["idx"]!));
        Assert.Equal(70, items[0]["qty"]);
        Assert.Equal(ink["name"], items[0]["name"]);   // kept rows keep their ids
        Assert.Equal(pen["name"], items[1]["name"]);
        Assert.NotEqual(book["name"], items[2]["name"]); // the removed row's id is gone
        Assert.DoesNotContain(items, i => (string)i["item_name"]! == "Book");
        Assert.True((DateTime)updated["modified"]! > (DateTime)doc["modified"]!); // a table-only change still counts as a change
    }

    [Fact]
    public async Task Rows_can_only_be_referenced_by_the_document_that_owns_them()
    {
        var first = await CreateOrderAsync(null, Row("Pen", 2));
        await _engine.Documents.InsertAsync("Sales Order", Values(("title", "B"), ("items", new[] { Row("Other", 1) })), TestUsers.Manager);
        var otherRow = Items(await _engine.Documents.GetAsync("Sales Order", "SO-0002", TestUsers.Manager))[0];

        var hijack = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Sales Order", "SO-0001",
                Values(("items", new[] { Values(("name", otherRow["name"]), ("qty", 99)) })), TestUsers.Manager));
        Assert.Contains(hijack.Errors, e => e.Field == "items[0].name");

        var rowName = Items(first)[0]["name"];
        var twice = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Sales Order", "SO-0001",
                Values(("items", new[] { Values(("name", rowName)), Values(("name", rowName)) })), TestUsers.Manager));
        Assert.Contains(twice.Errors, e => e.Field == "items[1].name");

        // The other document is untouched.
        Assert.Equal(1, Items(await _engine.Documents.GetAsync("Sales Order", "SO-0002", TestUsers.Manager))[0]["qty"]);
    }

    [Fact]
    public async Task A_stale_edit_of_only_the_table_is_still_a_conflict()
    {
        var doc = await CreateOrderAsync();
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Sales Order", "SO-0001", Values(("title", "X")), TestUsers.Manager);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _engine.Documents.UpdateAsync("Sales Order", "SO-0001",
                Values(("items", new[] { Row("Pen", 9) }), ("modified", doc["modified"])), TestUsers.Manager));
    }

    [Fact]
    public async Task An_update_cannot_empty_a_required_table_but_can_empty_an_optional_one()
    {
        await _engine.Documents.InsertAsync("Sales Order", Values(
            ("title", "T"), ("items", new[] { Row("Pen", 1) }), ("notes", new[] { Values(("note", "fragile")) })), TestUsers.Manager);

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Sales Order", "SO-0001", Values(("items", new List<object>())), TestUsers.Manager));

        var cleared = await _engine.Documents.UpdateAsync("Sales Order", "SO-0001", Values(("notes", new List<object>())), TestUsers.Manager);
        Assert.Empty((List<Dictionary<string, object?>>)cleared["notes"]!);
        Assert.Single(Items(cleared));
    }

    [Fact]
    public async Task Deleting_the_document_deletes_its_rows()
    {
        await CreateOrderAsync(null, Row("Pen", 2), Row("Book", 5));
        var child = _engine.Registry.Get("Order Item");
        Assert.Equal(2, await _engine.Repository.CountAsync(child, new ListQuery()));

        await _engine.Documents.DeleteAsync("Sales Order", "SO-0001", TestUsers.Manager);

        Assert.Equal(0, await _engine.Repository.CountAsync(child, new ListQuery()));
    }

    [Fact]
    public async Task Child_doctypes_cannot_be_reached_directly()
    {
        await CreateOrderAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.ListAsync("Order Item", new ListRequest(), TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("Order Item", "x", TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.InsertAsync("Order Item", Row("Pen", 1), TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.UpdateAsync("Order Item", "x", Row("Pen", 1), TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.DeleteAsync("Order Item", "x", TestUsers.Admin));
        Assert.Throws<NotFoundException>(() => _engine.Documents.GetMeta("Order Item", TestUsers.Admin));
        Assert.DoesNotContain("Order Item", _engine.Documents.ListMetas(TestUsers.Admin).Select(m => m.Name));
    }

    [Fact]
    public async Task Rows_follow_the_permissions_of_their_parent()
    {
        var mine = await CreateOrderAsync(TestUsers.SalesA, Row("Pen", 1));
        await _engine.Documents.InsertAsync("Sales Order", Values(("title", "B"), ("items", new[] { Row("Secret", 1) })), TestUsers.SalesB);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Sales Order", "SO-0002", TestUsers.SalesA));
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.UpdateAsync("Sales Order", "SO-0002", Values(("items", new[] { Row("Mine", 1) })), TestUsers.SalesA));
        await _engine.Documents.UpdateAsync("Sales Order", (string)mine["name"]!, Values(("items", new[] { Row("Pen", 3) })), TestUsers.SalesA);
    }

    [Fact]
    public void The_meta_endpoint_can_ship_the_child_definitions_with_the_parent()
    {
        var children = _engine.Documents.GetChildMetas(_engine.Registry.Get("Sales Order"));

        Assert.Equal(["Order Item", "Order Note"], children.Keys.Order());
        Assert.True(children["Order Item"].IsChildTable);
    }

    [Fact]
    public async Task A_failed_write_leaves_neither_the_document_nor_any_rows()
    {
        var parent = _engine.Registry.Get("Sales Order");
        var child = _engine.Registry.Get("Order Item");
        var now = DateTime.UtcNow;
        IReadOnlyDictionary<string, object?> ParentRow(string name) => Values(
            ("name", name), ("title", "T"), ("creation", now), ("modified", now), ("modified_by", "x"), ("owner", "x"), ("docstatus", 0), ("idx", 0));
        IReadOnlyDictionary<string, object?> ChildRow(string name) => Values(
            ("name", name), ("parent", "P1"), ("parenttype", "Sales Order"), ("parentfield", "items"), ("idx", 1),
            ("item_name", "x"), ("qty", 1), ("creation", now), ("modified", now), ("modified_by", "x"), ("owner", "x"), ("docstatus", 0));

        var clash = new ChildChanges(child, [ChildRow("dup"), ChildRow("dup")], [], []);
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Repository.InsertAsync(parent, ParentRow("P1"), [clash]));

        Assert.Null(await _engine.Repository.GetAsync(parent, "P1"));
        Assert.Equal(0, await _engine.Repository.CountAsync(child, new ListQuery()));
    }

    [Fact]
    public void Child_tables_get_extra_validation_rules()
    {
        var errors = DocTypeLoader.Check(DocTypeLoader.Parse(OrderItem) with
        {
            Autoname = "SO-.####",
            Fields = [new DocFieldMeta { Name = "a", Type = FieldType.Data, Unique = true }, new DocFieldMeta { Name = "t", Type = FieldType.Table, Options = "Order Note" }],
        });

        var paths = errors.Select(e => e.Field).ToHashSet();
        Assert.Contains("autoname", paths);
        Assert.Contains("fields[0].unique", paths);
        Assert.Contains("fields[1].type", paths); // no table inside a table

        var table = DocTypeLoader.Check(DocTypeLoader.Parse(SalesOrder) with
        {
            Fields = [new DocFieldMeta { Name = "items", Type = FieldType.Table, Options = "Order Item", Unique = true, Default = "x" }],
        });
        Assert.Contains(table, e => e.Field == "fields[0].unique");
        Assert.Contains(table, e => e.Field == "fields[0].default");
    }

    [Fact]
    public void A_table_must_point_at_a_child_doctype()
    {
        var registry = new MetaRegistry();
        registry.Add(DocTypeLoader.Parse(SalesOrder));
        registry.Add(DocTypeLoader.Parse(OrderNote));

        Assert.Throws<DocTypeValidationException>(registry.ValidateReferences); // "Order Item" is missing
    }

    [Fact]
    public async Task Designer_creates_child_and_parent_doctypes_and_they_work_together()
    {
        var designer = _engine.Designer;
        await designer.CreateAsync(new DocTypeMeta
        {
            Name = "Line",
            IsChildTable = true,
            Fields = [new DocFieldMeta { Name = "what", Type = FieldType.Data, Required = true }],
        }, TestUsers.Admin);
        var parent = await designer.CreateAsync(new DocTypeMeta
        {
            Name = "Invoice",
            Fields = [new DocFieldMeta { Name = "lines", Type = FieldType.Table, Options = "Line" }],
        }, TestUsers.Admin);

        var doc = await _engine.Documents.InsertAsync("Invoice", Values(("lines", new[] { Values(("what", "A")), Values(("what", "B")) })), TestUsers.Admin);

        Assert.Equal(2, ((List<Dictionary<string, object?>>)doc["lines"]!).Count);
        Assert.Equal(DocTypeSource.Custom, parent.Source);

        // A child table cannot be deleted while a parent uses it, nor can its nature change.
        await Assert.ThrowsAsync<ConflictException>(() => designer.DeleteAsync("Line", TestUsers.Admin));
        var line = await designer.GetAsync("Line", TestUsers.Admin);
        var flipped = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            designer.UpdateAsync("Line", line.Definition with { IsChildTable = false }, line.Modified, TestUsers.Admin));
        Assert.Contains(flipped.Errors, e => e.Field == "isChildTable");
    }
}
