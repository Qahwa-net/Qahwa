using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ServerScriptTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public ServerScriptTests()
    {
        RegisterInvoice();
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "autoname": "TK-.####", "fields": [
                { "name": "customer", "type": "Data" }, { "name": "priority", "type": "Data" }, { "name": "amount", "type": "Float" },
                { "name": "note", "type": "Text" }, { "name": "pin", "type": "Password" }, { "name": "secret", "type": "Data", "permLevel": 1 } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Viewer", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Followup", "autoname": "FU-.####", "fields": [ { "name": "subject", "type": "Data", "required": true }, { "name": "ticket", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private void RegisterInvoice()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Invoice", "isSubmittable": true, "autoname": "INV-.####", "fields": [ { "name": "total", "type": "Float" }, { "name": "note", "type": "Text" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "submit": true, "cancel": true, "delete": true } ] }
            """));
    }

    private async Task<string> ScriptAsync(string source, string @event = "Validate", bool active = true, string title = "S")
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var created = await _engine.Documents.InsertAsync("Server Script", Values(("title", title), ("active", active ? 1 : 0), ("reference_doctype", "Ticket"), ("event", @event), ("script", source)), TestUsers.SystemManager);
        return (string)created["name"]!;
    }

    private async Task<Dictionary<string, object?>> TicketAsync(params (string, object?)[] values)
    {
        var all = new Dictionary<string, object?> { ["customer"] = "Acme", ["priority"] = "Low" };
        foreach (var (k, v) in values)
            all[k] = v;
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        return await _engine.Documents.InsertAsync("Ticket", all, Ann);
    }

    private async Task<DocumentValidationException> RefusedAsync(Func<Task> action) => await Assert.ThrowsAsync<DocumentValidationException>(action);

    private async Task<Dictionary<string, object?>> RowAsync(string name) => await _engine.Documents.GetAsync("Server Script", name, TestUsers.Admin);

    [Fact]
    public async Task A_validate_script_can_set_fields_before_saving()
    {
        await ScriptAsync("if (doc.priority === 'Low' && doc.amount > 1000) doc.priority = 'High'; doc.note = 'Checked by ' + user.name;");

        var small = await TicketAsync(("amount", 5));
        var big = await TicketAsync(("amount", 5000));

        Assert.Equal("Low", small["priority"]);
        Assert.Equal("High", big["priority"]);
        Assert.Equal("Checked by ann@example.com", big["note"]);
    }

    [Fact]
    public async Task It_sees_the_old_values_and_whether_the_document_is_new_when_updating()
    {
        await ScriptAsync("if (!isNew && old.priority !== doc.priority) doc.note = 'was ' + old.priority + ', now ' + doc.priority;");
        var ticket = await TicketAsync();

        var updated = await _engine.Documents.UpdateAsync("Ticket", (string)ticket["name"]!, Values(("priority", "High")), Ann);

        Assert.Equal("was Low, now High", updated["note"]);
        Assert.Null(ticket["note"]);
    }

    [Fact]
    public async Task A_script_can_refuse_the_change_and_nothing_is_saved()
    {
        await ScriptAsync("if (doc.amount < 0) qahwa.error('The amount cannot be negative.');");

        var error = await RefusedAsync(() => TicketAsync(("amount", -1)));

        Assert.Equal("The amount cannot be negative.", error.Errors.Single().Message);
        Assert.Empty((await _engine.Documents.ListAsync("Ticket", new ListRequest(), TestUsers.Admin)).Data);
        await TicketAsync(("amount", 1));
    }

    [Fact]
    public async Task Scripts_run_in_order_and_each_sees_the_one_before()
    {
        await ScriptAsync("doc.note = 'one';", title: "A");
        await ScriptAsync("doc.note = doc.note + ' two';", title: "B");

        Assert.Equal("one two", (await TicketAsync())["note"]);
    }

    [Fact]
    public async Task Inactive_scripts_and_other_DocTypes_are_left_alone()
    {
        await ScriptAsync("doc.note = 'ran';", active: false);
        await ScriptAsync("qahwa.error('never');", title: "Other"); // for Ticket, but...
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Server Script", Values(("title", "F"), ("active", 1), ("reference_doctype", "Followup"), ("event", "Validate"), ("script", "doc.ticket = 'seen';")), TestUsers.SystemManager);
        var followup = await _engine.Documents.InsertAsync("Followup", Values(("subject", "x")), Ann);

        Assert.Equal("seen", followup["ticket"]);
    }

    [Fact]
    public async Task A_script_cannot_change_system_fields_unknown_fields_tables_or_passwords()
    {
        foreach (var line in new[] { "doc.owner = 'someone-else';", "doc.docstatus = 1;", "doc.nonsense = 1;", "doc.pin = 'x';", "doc.name = 'TK-HACK';" })
        {
            var id = await ScriptAsync(line);
            await RefusedAsync(() => TicketAsync());
            await _engine.Documents.DeleteAsync("Server Script", id, TestUsers.SystemManager);
        }
    }

    [Fact]
    public async Task A_script_never_sees_passwords_and_cannot_read_what_the_person_cannot()
    {
        await ScriptAsync("doc.note = 'pin=' + doc.pin + ' secret=' + typeof qahwa.get('Ticket', 'none');");

        var ticket = await TicketAsync();

        Assert.Equal("pin=undefined secret=object", ticket["note"]); // typeof null is "object": nothing found
    }

    [Fact]
    public async Task What_a_script_reads_goes_through_the_permissions_of_the_person()
    {
        var made = await TicketAsync();
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Hidden", "autoname": "HD-.####", "fields": [ { "name": "x", "type": "Data" } ], "permissions": [ { "role": "Boss", "read": true, "write": true, "create": true } ] }
            """));
        var hidden = await _engine.Documents.InsertAsync("Hidden", Values(("x", "top secret")), new UserContext("boss@example.com", ["Boss"]));
        await ScriptAsync($"var h = qahwa.get('Hidden', '{hidden["name"]}'); doc.note = 'got ' + h.x;");

        var error = await Assert.ThrowsAnyAsync<QahwaException>(() => _engine.Documents.UpdateAsync("Ticket", (string)made["name"]!, Values(("priority", "High")), Ann));

        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task A_script_can_look_up_other_documents_within_limits()
    {
        var first = await TicketAsync(("amount", 10));
        await TicketAsync(("amount", 20));
        await ScriptAsync("var all = qahwa.list('Ticket', { fields: ['name', 'amount'], filters: [['amount', '>', 5]], limit: 500 }); doc.note = 'count ' + all.length;");

        var third = await TicketAsync(("amount", 30));

        Assert.Equal("count 2", third["note"]);
        Assert.NotNull(first);
    }

    [Fact]
    public async Task Too_many_reads_stop_the_script()
    {
        await ScriptAsync("for (var i = 0; i < 100; i++) qahwa.get('Ticket', 'x' + i);");

        var error = await RefusedAsync(() => TicketAsync());

        Assert.Contains("at most", error.Errors.Single().Message);
    }

    [Fact]
    public async Task A_validate_script_may_not_write_to_other_documents()
    {
        await ScriptAsync("qahwa.insert('Followup', { subject: 'sneaky' });");

        var error = await RefusedAsync(() => TicketAsync());

        Assert.Contains("may not create", error.Errors.Single().Message);
        Assert.Empty((await _engine.Documents.ListAsync("Followup", new ListRequest(), TestUsers.Admin)).Data);
    }

    [Fact]
    public async Task An_after_save_script_can_create_documents_as_the_person_and_cannot_start_more_scripts()
    {
        await ScriptAsync("qahwa.insert('Followup', { subject: 'Call ' + doc.customer, ticket: doc.name });", "After Save");
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Server Script", Values(("title", "F"), ("active", 1), ("reference_doctype", "Followup"), ("event", "After Save"), ("script", "qahwa.insert('Followup', { subject: 'loop' });")), TestUsers.SystemManager);

        var ticket = await TicketAsync();

        var followups = (await _engine.Documents.ListAsync("Followup", new ListRequest { Fields = ["subject", "ticket", "owner"], Limit = 100 }, TestUsers.Admin)).Data;
        var one = Assert.Single(followups); // the script on Followup did not run for what a script made: no loop
        Assert.Equal("Call Acme", one["subject"]);
        Assert.Equal(ticket["name"], one["ticket"]);
        Assert.Equal("ann@example.com", one["owner"]);
    }

    [Fact]
    public async Task An_after_save_script_that_fails_does_not_undo_the_save_and_says_what_went_wrong()
    {
        var id = await ScriptAsync("qahwa.insert('Followup', { subject: '' });", "After Save");

        var ticket = await TicketAsync();

        Assert.NotNull(await _engine.Documents.GetAsync("Ticket", (string)ticket["name"]!, TestUsers.Admin));
        Assert.NotNull((await RowAsync(id))["last_error"]);
    }

    [Fact]
    public async Task A_validate_script_that_is_broken_stops_saves_and_records_the_problem_until_fixed()
    {
        var id = await ScriptAsync("doc.missing.deeper = 1;");

        var error = await RefusedAsync(() => TicketAsync());

        Assert.StartsWith("The script stopped:", error.Errors.Single().Message);
        Assert.Contains("stopped", (string)(await RowAsync(id))["last_error"]!);
        await _engine.Documents.UpdateAsync("Server Script", id, Values(("script", "doc.note = 'fixed';")), TestUsers.SystemManager);
        Assert.Equal("fixed", (await TicketAsync())["note"]);
        Assert.Null((await RowAsync(id))["last_error"]);
    }

    [Theory]
    [InlineData("while (true) {}", "too much work")]
    [InlineData("doc.note = String(/(a+)+$/.test('a'.repeat(60) + 'b'));", "too long")]
    [InlineData("for (var i = 0; i < 1e9; i++) { var x = i * 2; }", "too much work")]
    [InlineData("function f() { return f(); } f();", "too deeply")]
    [InlineData("var s = 'x'; while (true) { s = s + s; }", "too much memory")]
    public async Task A_script_that_runs_away_is_stopped(string source, string expected)
    {
        await ScriptAsync(source);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var error = await RefusedAsync(() => TicketAsync());

        Assert.Contains(expected, error.Errors.Single().Message);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("doc.note = typeof require + typeof process + typeof System + typeof window + typeof fetch + typeof XMLHttpRequest + typeof setTimeout + typeof Deno;")]
    [InlineData("doc.note = typeof importNamespace + typeof clr + typeof host;")]
    public async Task The_script_has_no_door_to_the_machine(string source)
    {
        await ScriptAsync(source);

        var ticket = await TicketAsync();

        Assert.All(((string)ticket["note"]!).Split("undefined", StringSplitOptions.RemoveEmptyEntries), part => Assert.Empty(part));
    }

    [Theory]
    [InlineData("var t = doc.constructor.constructor('return typeof process')(); doc.note = t;")]
    [InlineData("doc.note = String(this.constructor.constructor('return typeof require')());")]
    public async Task Reaching_for_the_global_through_constructors_finds_nothing_dangerous(string source)
    {
        await ScriptAsync(source);

        var ticket = await TicketAsync();

        Assert.Equal("undefined", ticket["note"]);
    }

    [Fact]
    public async Task Scripts_cannot_see_dotnet_types_or_use_reflection()
    {
        await ScriptAsync("try { doc.note = String(doc.GetType()); } catch (e) { doc.note = 'blocked'; }");

        Assert.Equal("blocked", (await TicketAsync())["note"]);
    }

    [Fact]
    public async Task The_scripts_own_api_cannot_be_replaced_from_inside()
    {
        await ScriptAsync("try { qahwa.error = function () {}; } catch (e) {} qahwa.log('x'); if (doc.amount < 0) qahwa.error('still works');");

        await RefusedAsync(() => TicketAsync(("amount", -1)));
    }

    [Fact]
    public async Task Mistakes_in_the_source_are_found_when_the_script_is_saved()
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));

        var error = await RefusedAsync(() => _engine.Documents.InsertAsync("Server Script", Values(("title", "Bad"), ("active", 1), ("reference_doctype", "Ticket"), ("event", "Validate"), ("script", "if (doc.x {")), TestUsers.SystemManager));

        Assert.Equal("script", error.Errors.Single().Field);
        Assert.Contains("mistake", error.Errors.Single().Message);
        await RefusedAsync(() => _engine.Documents.InsertAsync("Server Script", Values(("title", "Big"), ("active", 1), ("reference_doctype", "Ticket"), ("event", "Validate"), ("script", new string(' ', 20_001) + "1;")), TestUsers.SystemManager));
    }

    [Fact]
    public async Task Only_a_system_manager_can_see_or_make_scripts()
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Server Script", Values(("title", "Mine"), ("active", 1), ("reference_doctype", "Ticket"), ("event", "Validate"), ("script", "1;")), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Server Script", new ListRequest(), Vic));
    }

    [Fact]
    public async Task A_saved_change_to_a_script_takes_effect_at_once_and_deleting_it_stops_it()
    {
        var id = await ScriptAsync("doc.note = 'v1';");
        Assert.Equal("v1", (await TicketAsync())["note"]);

        await _engine.Documents.UpdateAsync("Server Script", id, Values(("script", "doc.note = 'v2';")), TestUsers.SystemManager);
        Assert.Equal("v2", (await TicketAsync())["note"]);

        await _engine.Documents.DeleteAsync("Server Script", id, TestUsers.SystemManager);
        Assert.Null((await TicketAsync())["note"]);
    }

    [Fact]
    public async Task Values_a_script_sets_are_checked_like_any_other_input()
    {
        await ScriptAsync("doc.amount = 'not a number';");

        var error = await RefusedAsync(() => TicketAsync());

        Assert.Equal("amount", error.Errors.Single().Field);
    }

    [Fact]
    public async Task A_dry_run_shows_what_would_happen_and_cannot_write()
    {
        var meta = _engine.Registry.Get("Ticket");
        var doc = new Dictionary<string, object?> { ["customer"] = "Acme", ["amount"] = 3.0 };

        var ok = await _engine.ServerScripts.TestAsync(meta, "Validate", "qahwa.log('hello', doc.amount); doc.priority = 'High';", doc, true, TestUsers.SystemManager, default);
        Assert.Equal("High", ok.Changes["priority"]);
        Assert.Equal(["hello 3"], ok.Log);

        await RefusedAsync(() => _engine.ServerScripts.TestAsync(meta, "After Save", "qahwa.insert('Followup', { subject: 'x' });", doc, true, TestUsers.SystemManager, default));
        Assert.Empty((await _engine.Documents.ListAsync("Followup", new ListRequest(), TestUsers.Admin)).Data);
    }

    [Fact]
    public async Task The_log_is_kept_small()
    {
        var outcome = await _engine.ServerScripts.TestAsync(_engine.Registry.Get("Ticket"), "Validate", "for (var i = 0; i < 500; i++) qahwa.log('x'.repeat(5000));", new Dictionary<string, object?>(), true, TestUsers.SystemManager, default);

        Assert.Equal(ScriptSandbox.MaxLogLines, outcome.Log.Count);
        Assert.All(outcome.Log, line => Assert.True(line.Length <= ScriptSandbox.MaxLogLength));
    }

    private async Task<string> OrderScriptAsync(string source, string @event, string title = "O")
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        var created = await _engine.Documents.InsertAsync("Server Script", Values(("title", title), ("active", 1), ("reference_doctype", "Invoice"), ("event", @event), ("script", source)), TestUsers.SystemManager);
        return (string)created["name"]!;
    }

    private async Task<Dictionary<string, object?>> InvoiceAsync(double total = 10)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        return await _engine.Documents.InsertAsync("Invoice", Values(("total", total)), Ann);
    }

    [Fact]
    public async Task Before_submit_can_refuse_or_change_and_after_submit_can_follow_up()
    {
        await InvoiceAsync();
        await OrderScriptAsync("if (doc.total > 100) qahwa.error('Big invoices need approval.'); doc.note = 'submitted by ' + user.name;", "Before Submit");
        await OrderScriptAsync("qahwa.insert('Followup', { subject: 'Submitted ' + doc.name });", "After Submit", "F");
        var small = await InvoiceAsync(10);
        var big = await InvoiceAsync(500);

        var error = await RefusedAsync(() => _engine.Documents.SubmitAsync("Invoice", (string)big["name"]!, Ann));
        Assert.Contains("approval", error.Errors.Single().Message);
        Assert.Equal(0, Convert.ToInt32((await _engine.Documents.GetAsync("Invoice", (string)big["name"]!, Ann))["docstatus"]));

        var submitted = await _engine.Documents.SubmitAsync("Invoice", (string)small["name"]!, Ann);
        Assert.Equal("submitted by ann@example.com", submitted["note"]);
        Assert.Equal($"Submitted {small["name"]}", Assert.Single((await _engine.Documents.ListAsync("Followup", new ListRequest { Fields = ["subject"] }, TestUsers.Admin)).Data)["subject"]);
    }

    [Fact]
    public async Task Before_cancel_can_refuse_and_validate_does_not_run_on_cancel()
    {
        var invoice = await InvoiceAsync();
        var name = (string)invoice["name"]!;
        var fine = await InvoiceAsync();
        await _engine.Documents.SubmitAsync("Invoice", name, Ann);
        await OrderScriptAsync("qahwa.error('Never validates');", "Validate", "V");
        await OrderScriptAsync("qahwa.error('Cancelling is not allowed.');", "Before Cancel", "C");

        var error = await RefusedAsync(() => _engine.Documents.CancelAsync("Invoice", name, Ann));

        Assert.Equal("Cancelling is not allowed.", error.Errors.Single().Message);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Invoice", (string)fine["name"]!, Ann)); // Validate does run on submit
    }

    [Fact]
    public async Task Before_delete_can_refuse_a_deletion_and_scripts_in_it_cannot_change_things()
    {
        var invoice = await InvoiceAsync(1000);
        var name = (string)invoice["name"]!;
        var id = await OrderScriptAsync("if (doc.total >= 1000) qahwa.error('Large invoices must be kept.');", "Before Delete");

        await RefusedAsync(() => _engine.Documents.DeleteAsync("Invoice", name, Ann));
        Assert.NotNull(await _engine.Documents.GetAsync("Invoice", name, Ann));

        await _engine.Documents.UpdateAsync("Server Script", id, Values(("script", "doc.note = 'x';")), TestUsers.SystemManager);
        await RefusedAsync(() => _engine.Documents.DeleteAsync("Invoice", name, Ann)); // changes are not allowed here

        await _engine.Documents.UpdateAsync("Server Script", id, Values(("active", 0)), TestUsers.SystemManager);
        await _engine.Documents.DeleteAsync("Invoice", name, Ann);
    }

    [Fact]
    public async Task After_cancel_runs_once_the_document_is_cancelled()
    {
        var invoice = await InvoiceAsync();
        var name = (string)invoice["name"]!;
        await OrderScriptAsync("qahwa.insert('Followup', { subject: 'Cancelled ' + doc.name });", "After Cancel");
        await _engine.Documents.SubmitAsync("Invoice", name, Ann);
        Assert.Empty((await _engine.Documents.ListAsync("Followup", new ListRequest(), TestUsers.Admin)).Data);

        await _engine.Documents.CancelAsync("Invoice", name, Ann);

        Assert.Single((await _engine.Documents.ListAsync("Followup", new ListRequest(), TestUsers.Admin)).Data);
    }
}
