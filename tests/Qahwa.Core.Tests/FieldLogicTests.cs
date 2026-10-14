using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class FieldLogicTests
{
    private const string Line = """
        {
          "name": "Doc Line", "isChildTable": true,
          "fields": [
            { "name": "item", "type": "Data", "required": true },
            { "name": "qty", "type": "Int" },
            { "name": "rate", "type": "Currency" },
            { "name": "amount", "type": "Currency", "formula": "qty * rate" },
            { "name": "doc_discount", "type": "Percent", "formula": "parent.discount ?? 0" },
            { "name": "note", "type": "Data", "dependsOn": "amount > 1000", "mandatoryDependsOn": "amount > 1000" }
          ],
          "validations": [ { "expression": "empty(qty) || qty > 0", "message": "Quantity must be positive", "field": "qty" } ]
        }
        """;

    private const string Doc = """
        {
          "name": "Sales Doc", "autoname": "SD-.####",
          "fields": [
            { "name": "title", "type": "Data", "required": true },
            { "name": "customer", "type": "Link", "options": "Customer" },
            { "name": "customer_name", "type": "Data", "fetchFrom": "customer.customer_name" },
            { "name": "order_date", "type": "Date", "default": "=today()" },
            { "name": "due_date", "type": "Date", "formula": "addDays(order_date, 30)" },
            { "name": "created_by", "type": "Data", "default": "=user()" },
            { "name": "status", "type": "Select", "options": "Draft\nOpen\nClosed", "default": "Draft" },
            { "name": "closed_reason", "type": "Data", "dependsOn": "status == 'Closed'", "mandatoryDependsOn": "status == 'Closed'" },
            { "name": "approver", "type": "Data", "required": true, "dependsOn": "status == 'Open'" },
            { "name": "discount", "type": "Percent" },
            { "name": "lines", "type": "Table", "options": "Doc Line" },
            { "name": "total", "type": "Currency", "formula": "sum(lines, 'amount')" },
            { "name": "net", "type": "Currency", "formula": "round(total * (1 - discount / 100), 2)" }
          ],
          "validations": [
            { "expression": "empty(due_date) || empty(order_date) || due_date >= order_date", "message": "Due date cannot be before the order date", "field": "due_date" },
            { "expression": "status != 'Closed' || count(lines) > 0", "message": "A closed document needs at least one line" }
          ],
          "permissions": [
            { "role": "Sales Manager", "read": true, "write": true, "create": true, "delete": true },
            { "role": "Sales User", "read": true, "write": true, "create": true, "ifOwner": true }
          ]
        }
        """;

    private readonly TestEngine _engine = new();

    public FieldLogicTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse(Line));
        _engine.Registry.Add(DocTypeLoader.Parse(Doc));
        _engine.Registry.ValidateReferences();
    }

    private static Dictionary<string, object?> Row(string item, int? qty, decimal? rate) =>
        Values(("item", item), ("qty", qty), ("rate", rate));

    private async Task<Dictionary<string, object?>> CreateAsync(IUserContext? user = null, params (string, object?)[] extra) =>
        await _engine.Documents.InsertAsync(
            "Sales Doc",
            Values([("title", "T"), ("approver", "boss"), .. extra]),
            user ?? TestUsers.Manager);

    private static List<Dictionary<string, object?>> Lines(Dictionary<string, object?> doc) =>
        (List<Dictionary<string, object?>>)doc["lines"]!;

    [Fact]
    public async Task Insert_calculates_row_amounts_totals_and_dates()
    {
        var doc = await CreateAsync(null,
            ("discount", 10),
            ("lines", new[] { Row("Pen", 2, 1.5m), Row("Book", 5, 10m) }));

        Assert.Equal([3m, 50m], Lines(doc).Select(l => (decimal)l["amount"]!));
        Assert.Equal(53m, doc["total"]);
        Assert.Equal(47.7m, doc["net"]);
        Assert.Equal(new DateOnly(2026, 1, 1), doc["order_date"]);   // default "=today()"
        Assert.Equal(new DateOnly(2026, 1, 31), doc["due_date"]);    // formula "addDays(order_date, 30)"
        Assert.Equal("manager@example.com", doc["created_by"]);      // default "=user()"
        Assert.Equal("Draft", doc["status"]);                         // an ordinary default still works
    }

    [Fact]
    public async Task A_calculated_field_always_wins_over_what_the_client_sends()
    {
        var doc = await CreateAsync(null,
            ("total", 999), ("lines", new[] { Values(("item", "Pen"), ("qty", 2), ("rate", 1), ("amount", 12345)) }));

        Assert.Equal(2m, Lines(doc)[0]["amount"]);
        Assert.Equal(2m, doc["total"]);
    }

    [Fact]
    public async Task Rows_with_missing_numbers_calculate_to_nothing_instead_of_failing()
    {
        var doc = await CreateAsync(null, ("lines", new[] { Row("Pen", 2, null) }));

        Assert.Null(Lines(doc)[0]["amount"]);
        Assert.Equal(0m, doc["total"]);
    }

    [Fact]
    public async Task Editing_a_row_recalculates_the_row_and_the_totals()
    {
        var doc = await CreateAsync(null, ("discount", 10), ("lines", new[] { Row("Pen", 2, 1.5m), Row("Book", 5, 10m) }));
        var pen = Lines(doc)[0]["name"];
        var book = Lines(doc)[1]["name"];
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("lines", new[]
        {
            Values(("name", pen), ("qty", 4)),
            Values(("name", book)),
        })), TestUsers.Manager);

        Assert.Equal([6m, 50m], Lines(updated).Select(l => (decimal)l["amount"]!));
        Assert.Equal(56m, updated["total"]);
        Assert.Equal(50.4m, updated["net"]);
    }

    [Fact]
    public async Task Changing_a_document_field_updates_totals_without_resending_the_rows()
    {
        await CreateAsync(null, ("discount", 0), ("lines", new[] { Row("Pen", 2, 5m) }));
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("discount", 50)), TestUsers.Manager);

        Assert.Equal(5m, updated["net"]);
        Assert.Equal(10m, updated["total"]);
    }

    [Fact]
    public async Task A_row_formula_can_read_the_parent_and_follows_it()
    {
        await CreateAsync(null, ("discount", 5), ("lines", new[] { Row("Pen", 1, 1m) }));
        Assert.Equal(5m, Lines(await _engine.Documents.GetAsync("Sales Doc", "SD-0001", TestUsers.Manager))[0]["doc_discount"]);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("discount", 20)), TestUsers.Manager);

        Assert.Equal(20m, Lines(updated)[0]["doc_discount"]); // saved rows were recalculated although the client sent none
    }

    [Fact]
    public async Task Formulas_follow_their_dependencies_not_the_order_of_the_fields()
    {
        // "net" reads "total" and is declared after it; here they are declared the other way round.
        var meta = DocTypeLoader.Parse("""
            { "name": "Backwards", "fields": [
                { "name": "c", "type": "Int", "formula": "b + 1" },
                { "name": "b", "type": "Int", "formula": "a + 1" },
                { "name": "a", "type": "Int" } ] }
            """);
        _engine.Registry.Add(meta);

        var doc = await _engine.Documents.InsertAsync("Backwards", Values(("a", 1)), TestUsers.Admin);

        Assert.Equal(2, doc["b"]);
        Assert.Equal(3, doc["c"]);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_does_not_touch_the_document()
    {
        var doc = await CreateAsync(null, ("lines", new[] { Row("Pen", 2, 1m) }));
        _engine.Clock.Advance(TimeSpan.FromMinutes(5));

        var again = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("title", "T"), ("discount", null)), TestUsers.Manager);

        Assert.Equal(doc["modified"], again["modified"]);
    }

    [Fact]
    public async Task A_condition_can_make_a_field_required_and_a_hidden_field_is_never_required()
    {
        // "approver" is always required but only while status is Open; "closed_reason" is required only when Closed.
        var draft = await _engine.Documents.InsertAsync("Sales Doc", Values(("title", "T")), TestUsers.Manager);
        Assert.Equal("Draft", draft["status"]);

        var open = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Doc", Values(("title", "T"), ("status", "Open")), TestUsers.Manager));
        Assert.Contains(open.Errors, e => e.Field == "approver");
        Assert.DoesNotContain(open.Errors, e => e.Field == "closed_reason");

        var closed = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Doc",
                Values(("title", "T"), ("status", "Closed"), ("lines", new[] { Row("Pen", 1, 1m) })), TestUsers.Manager));
        Assert.Contains(closed.Errors, e => e.Field == "closed_reason");
        Assert.DoesNotContain(closed.Errors, e => e.Field == "approver");

        await _engine.Documents.InsertAsync("Sales Doc",
            Values(("title", "T"), ("status", "Closed"), ("closed_reason", "done"), ("lines", new[] { Row("Pen", 1, 1m) })),
            TestUsers.Manager);
    }

    [Fact]
    public async Task Required_rows_fields_follow_the_row_s_own_condition()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            CreateAsync(null, ("lines", new[] { Row("Cheap", 1, 5m), Row("Dear", 3, 500m) })));

        Assert.Contains(ex.Errors, e => e.Field == "lines[1].note"); // amount 1500 makes "note" mandatory
        Assert.DoesNotContain(ex.Errors, e => e.Field == "lines[0].note");
    }

    [Fact]
    public async Task Rules_refuse_a_document_with_the_message_on_the_named_field()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Doc", Values(("title", "T"), ("approver", "x"), ("status", "Closed"), ("closed_reason", "r")), TestUsers.Manager));

        var error = Assert.Single(ex.Errors);
        Assert.Equal("", error.Field); // a rule without a field applies to the document
        Assert.Equal("A closed document needs at least one line", error.Message);
    }

    [Fact]
    public async Task Row_rules_name_the_row_and_the_field()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            CreateAsync(null, ("lines", new[] { Row("Pen", 2, 1m), Row("Bad", 0, 1m) })));

        var error = Assert.Single(ex.Errors);
        Assert.Equal("lines[1].qty", error.Field);
        Assert.Equal("Quantity must be positive", error.Message);
    }

    [Fact]
    public async Task Rules_wait_until_nothing_more_basic_is_wrong()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Sales Doc", Values(("status", "Closed")), TestUsers.Manager));

        Assert.Contains(ex.Errors, e => e.Field == "title");
        Assert.DoesNotContain(ex.Errors, e => e.Message.Contains("closed document"));
    }

    [Fact]
    public async Task A_rule_can_read_the_calculated_values()
    {
        var meta = DocTypeLoader.Parse("""
            { "name": "Budgeted", "fields": [
                { "name": "price", "type": "Currency" }, { "name": "qty", "type": "Int" },
                { "name": "total", "type": "Currency", "formula": "price * qty" } ],
              "validations": [ { "expression": "total <= 100", "message": "Over budget", "field": "price" } ] }
            """);
        _engine.Registry.Add(meta);

        await _engine.Documents.InsertAsync("Budgeted", Values(("price", 10), ("qty", 10)), TestUsers.Admin);
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Budgeted", Values(("price", 10), ("qty", 11)), TestUsers.Admin));
        Assert.Equal("price", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public async Task Fetch_from_copies_a_value_from_the_linked_document_and_follows_the_link()
    {
        var acme = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Manager);
        var globex = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Globex")), TestUsers.Manager);

        var doc = await CreateAsync(null, ("customer", acme["name"]));
        Assert.Equal("Acme", doc["customer_name"]);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var moved = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("customer", globex["name"])), TestUsers.Manager);
        Assert.Equal("Globex", moved["customer_name"]);

        var cleared = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("customer", "")), TestUsers.Manager);
        Assert.Null(cleared["customer_name"]);
    }

    [Fact]
    public async Task Fetch_from_is_not_repeated_while_the_link_is_unchanged()
    {
        var acme = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Manager);
        await CreateAsync(null, ("customer", acme["name"]));
        await _engine.Documents.UpdateAsync("Customer", (string)acme["name"]!, Values(("customer_name", "Acme Renamed")), TestUsers.Manager);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Sales Doc", "SD-0001", Values(("title", "new title"), ("customer", acme["name"])), TestUsers.Manager);

        Assert.Equal("Acme", updated["customer_name"]); // a snapshot: it only refreshes when the link changes
    }

    [Fact]
    public async Task Fetch_from_never_copies_data_the_user_may_not_read()
    {
        var acme = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Secret Co")), TestUsers.SalesB);

        // SalesA may only read their own customers, and this one belongs to SalesB.
        var doc = await _engine.Documents.InsertAsync(
            "Sales Doc", Values(("title", "T"), ("approver", "x"), ("customer", acme["name"])), TestUsers.SalesA);

        Assert.Null(doc["customer_name"]);
    }

    [Fact]
    public async Task Fetch_if_empty_keeps_what_the_user_typed()
    {
        _engine.Registry.Upsert(DocTypeLoader.Parse("""
            { "name": "Memo", "fields": [
                { "name": "customer", "type": "Link", "options": "Customer" },
                { "name": "label", "type": "Data", "fetchFrom": "customer.customer_name", "fetchIfEmpty": true } ] }
            """), DocTypeSource.App);
        var acme = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Manager);

        var typed = await _engine.Documents.InsertAsync("Memo", Values(("customer", acme["name"]), ("label", "mine")), TestUsers.Admin);
        var empty = await _engine.Documents.InsertAsync("Memo", Values(("customer", acme["name"])), TestUsers.Admin);

        Assert.Equal("mine", typed["label"]);
        Assert.Equal("Acme", empty["label"]);
    }

    [Fact]
    public async Task An_expression_default_applies_only_when_the_document_is_created()
    {
        var doc = await CreateAsync(null, ("created_by", null));
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync("Sales Doc", (string)doc["name"]!, Values(("title", "Renamed")), TestUsers.Admin);

        Assert.Equal("manager@example.com", doc["created_by"]);
        Assert.Equal("manager@example.com", updated["created_by"]); // not replaced by the editor
    }

    [Fact]
    public async Task A_formula_that_gives_a_value_the_field_cannot_hold_is_reported()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Splitter", "fields": [ { "name": "n", "type": "Int" }, { "name": "third", "type": "Int", "formula": "n / 3" } ] }
            """));

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Splitter", Values(("n", 10)), TestUsers.Admin));

        Assert.Equal("third", Assert.Single(ex.Errors).Field);
        await _engine.Documents.InsertAsync("Splitter", Values(("n", 9)), TestUsers.Admin);
    }

    [Fact]
    public async Task Calculated_and_fetched_values_are_saved_with_the_row_changes_in_one_write()
    {
        var doc = await CreateAsync(null, ("lines", new[] { Row("Pen", 2, 1m) }));
        var stored = await _engine.Repository.GetChildrenAsync(_engine.Registry.Get("Doc Line"), "Sales Doc", (string)doc["name"]!, "lines");

        Assert.Equal(2m, stored[0]["amount"]); // the formula result is in the database row, not only in the response
    }
}
