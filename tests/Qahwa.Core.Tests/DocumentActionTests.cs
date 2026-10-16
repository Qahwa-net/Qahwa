using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DocumentActionTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Visitor = new("vic@example.com", ["Viewer"]);

    public DocumentActionTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "isSubmittable": true, "autoname": "TK-.####", "fields": [
                { "name": "customer", "type": "Data" }, { "name": "priority", "type": "Data" }, { "name": "status", "type": "Data" },
                { "name": "note", "type": "Text" }, { "name": "pin", "type": "Password" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "submit": true, "cancel": true }, { "role": "Viewer", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Followup", "autoname": "FU-.####", "fields": [
                { "name": "subject", "type": "Data", "required": true }, { "name": "who", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task ActionAsync(string title, string @event = "New", string action = "Set Field", string? targetField = "note", string? value = "Hello",
        string? condition = null, string? watch = null, string? targetDoctype = null, string? mapping = null, bool active = true)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Document Action", Values(("title", title), ("active", active ? 1 : 0), ("reference_doctype", "Ticket"), ("event", @event), ("watch_field", watch),
            ("condition", condition), ("action", action), ("target_field", targetField), ("value", value), ("target_doctype", targetDoctype), ("mapping", mapping)), TestUsers.SystemManager);
    }

    private async Task<Dictionary<string, object?>> TicketAsync(UserContext? user = null, params (string, object?)[] extra)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var values = Values(("customer", "Acme"), ("priority", "Low"), ("status", "Open"));
        foreach (var (k, v) in extra)
            values[k] = v;
        var created = await _engine.Documents.InsertAsync("Ticket", values, user ?? Ann);
        return (await _engine.Documents.GetAsync("Ticket", (string)created["name"]!, TestUsers.Admin));
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> FollowupsAsync() =>
        (await _engine.Documents.ListAsync("Followup", new ListRequest { Fields = ["name", "subject", "who", "owner"], Limit = 100, OrderBy = "creation", Descending = false }, TestUsers.Admin)).Data;

    private async Task<Dictionary<string, object?>> RowAsync(string title) =>
        (await _engine.Documents.ListAsync("Document Action", new ListRequest { Fields = ["name", "last_error", "run_count"], Filters = [new RawFilter("title", "=", title)], Limit = 1 }, TestUsers.Admin)).Data.Single();

    [Fact]
    public async Task A_new_document_gets_a_field_set_from_a_template()
    {
        await ActionAsync("Note", value: "Opened by {{user}} for {{customer}} ({{name}})");

        var ticket = await TicketAsync();

        Assert.Equal($"Opened by ann@example.com for Acme ({ticket["name"]})", ticket["note"]);
    }

    [Fact]
    public async Task A_condition_decides_and_inactive_actions_do_nothing()
    {
        await ActionAsync("High only", condition: "priority == 'High'", value: "urgent");
        await ActionAsync("Off", targetField: "status", value: "Changed", active: false);

        var low = await TicketAsync();
        var high = await TicketAsync(null, ("priority", "High"));

        Assert.Null(low["note"]);
        Assert.Equal("urgent", high["note"]);
        Assert.Equal("Open", high["status"]);
    }

    [Fact]
    public async Task A_changed_value_can_create_another_document()
    {
        await ActionAsync("Follow up", @event: "ValueChange", watch: "status", action: "Create Document", targetField: null, value: null, targetDoctype: "Followup", mapping: "subject = Ticket {{name}} is now {{status}}\nwho = {{customer}}");
        var ticket = await TicketAsync();
        Assert.Empty(await FollowupsAsync());

        await _engine.Documents.UpdateAsync("Ticket", (string)ticket["name"]!, Values(("priority", "High")), Ann);
        Assert.Empty(await FollowupsAsync()); // another field changed
        await _engine.Documents.UpdateAsync("Ticket", (string)ticket["name"]!, Values(("status", "Closed")), Ann);

        var follow = Assert.Single(await FollowupsAsync());
        Assert.Equal($"Ticket {ticket["name"]} is now Closed", follow["subject"]);
        Assert.Equal("Acme", follow["who"]);
        Assert.Equal("ann@example.com", follow["owner"]); // made by the person, with their rights
        Assert.Equal(1, Convert.ToInt32((await RowAsync("Follow up"))["run_count"]));
    }

    [Fact]
    public async Task Submitting_can_start_an_action()
    {
        await ActionAsync("On submit", @event: "Submit", action: "Create Document", targetField: null, value: null, targetDoctype: "Followup", mapping: "subject = Submitted {{name}}");
        var ticket = await TicketAsync();
        Assert.Empty(await FollowupsAsync());

        await _engine.Documents.SubmitAsync("Ticket", (string)ticket["name"]!, Ann);

        Assert.Equal($"Submitted {ticket["name"]}", Assert.Single(await FollowupsAsync())["subject"]);
    }

    [Fact]
    public async Task Actions_do_not_start_actions_so_they_cannot_loop()
    {
        await ActionAsync("Every change", @event: "Update", targetField: "note", value: "touched");
        await ActionAsync("Second", @event: "Update", targetField: "status", value: "Seen");
        var ticket = await TicketAsync();

        await _engine.Documents.UpdateAsync("Ticket", (string)ticket["name"]!, Values(("priority", "High")), Ann);

        var after = await _engine.Documents.GetAsync("Ticket", (string)ticket["name"]!, TestUsers.Admin);
        Assert.Equal("touched", after["note"]);
        Assert.Equal("Seen", after["status"]);
        Assert.Equal(1, Convert.ToInt32((await RowAsync("Every change"))["run_count"]));
    }

    [Fact]
    public async Task Actions_run_with_the_rights_of_the_person_and_a_failure_never_fails_the_save()
    {
        await ActionAsync("Make follow up", action: "Create Document", targetField: null, value: null, targetDoctype: "Followup", mapping: "subject = Hi");

        var ticket = await TicketAsync(Visitor); // a Viewer may not create Followups

        Assert.Equal("Acme", ticket["customer"]); // the ticket itself was saved
        Assert.Empty(await FollowupsAsync());
        Assert.Contains("permission", (string)(await RowAsync("Make follow up"))["last_error"]!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Set Field", "pin", "x", null, null)] // a password
    [InlineData("Set Field", "nope", "x", null, null)] // not a field
    [InlineData("Set Field", "name", "x", null, null)] // a system column
    [InlineData("Set Field", "note", "{{a.b}}", null, null)] // not a plain placeholder
    [InlineData("Set Field", "note", "{{1+1}}", null, null)]
    [InlineData("Set Field", "note", "{{missing_field}}", null, null)]
    [InlineData("Set Field", "note", "", null, null)]
    [InlineData("Create Document", null, null, "Role", "role_name = Boss")] // a framework DocType
    [InlineData("Create Document", null, null, "Followup", "owner = someone")] // not a field
    [InlineData("Create Document", null, null, "Followup", "subject = {{x.y}}")]
    [InlineData("Create Document", null, null, "Followup", "")]
    public async Task An_action_that_is_wrong_changes_nothing_and_says_why(string action, string? field, string? value, string? doctype, string? mapping)
    {
        await ActionAsync("Bad", action: action, targetField: field, value: value, targetDoctype: doctype, mapping: mapping);

        var ticket = await TicketAsync();

        Assert.Null(ticket["note"]);
        Assert.Empty(await FollowupsAsync());
        Assert.False(string.IsNullOrEmpty((string?)(await RowAsync("Bad"))["last_error"]));
        Assert.Equal(0, Convert.ToInt32((await RowAsync("Bad"))["run_count"] ?? 0));
    }

    [Fact]
    public async Task The_doctype_of_an_action_must_exist_when_it_is_saved()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => ActionAsync("Nowhere", action: "Create Document", targetField: null, value: null, targetDoctype: "Nowhere", mapping: "subject = Hi"));
    }

    [Fact]
    public async Task Values_are_plain_text_placeholders_are_not_expanded_twice_and_sql_is_just_text()
    {
        await ActionAsync("Copy", targetField: "note", value: "{{customer}}");

        var ticket = await TicketAsync(null, ("customer", "{{priority}}'; drop table \"tabTicket\"; --"));

        Assert.Equal("{{priority}}'; drop table \"tabTicket\"; --", ticket["note"]);
        Assert.NotEmpty((await _engine.Documents.ListAsync("Ticket", new ListRequest { Limit = 5 }, TestUsers.Admin)).Data);
    }

    [Fact]
    public async Task Only_a_system_manager_can_write_actions()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Document Action",
            Values(("title", "x"), ("reference_doctype", "Ticket"), ("event", "New"), ("action", "Set Field"), ("target_field", "note"), ("value", "x")), Ann));
    }

    [Fact]
    public async Task Changing_an_action_takes_effect_at_once_on_this_server()
    {
        await ActionAsync("Note", value: "one");
        Assert.Equal("one", (await TicketAsync())["note"]);

        var row = await RowAsync("Note");
        await _engine.Documents.UpdateAsync("Document Action", (string)row["name"]!, Values(("value", "two")), TestUsers.SystemManager);

        Assert.Equal("two", (await TicketAsync())["note"]);
        await _engine.Documents.DeleteAsync("Document Action", (string)row["name"]!, TestUsers.SystemManager);
        Assert.Null((await TicketAsync())["note"]);
    }
}
