using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AppTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public AppTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Deal", "isSubmittable": true, "autoname": "DL-.####", "fields": [
                { "name": "title", "type": "Data" }, { "name": "amount", "type": "Float" }, { "name": "tier", "type": "Data" }, { "name": "pin", "type": "Password" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true, "submit": true, "cancel": true }, { "role": "Viewer", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Audit Note", "autoname": "AN-.####", "fields": [ { "name": "text", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private sealed class SalesApp(Action<IAppBuilder> configure, string name = "sales", string version = "1.0.0") : IQahwaApp
    {
        public string Name => name;
        public string Version => version;
        public void Configure(IAppBuilder app) => configure(app);
    }

    private void Install(Action<IAppBuilder> configure, string name = "sales", string version = "1.0.0") => _engine.AppCatalog.Add(new SalesApp(configure, name, version));

    private Task<Dictionary<string, object?>> DealAsync(double amount = 10, UserContext? user = null, string title = "Big Deal") =>
        _engine.Documents.InsertAsync("Deal", Values(("title", title), ("amount", amount)), user ?? Ann);

    private async Task<string[]> NotesAsync() =>
        (await _engine.Documents.ListAsync("Audit Note", new ListRequest { Fields = ["text"], OrderBy = "creation", Descending = false, Limit = 100 }, TestUsers.Admin)).Data.Select(r => (string)r["text"]!).ToArray();

    // ---- hooks ----

    [Fact]
    public async Task A_before_save_hook_can_change_fields_and_sees_whether_the_document_is_new()
    {
        Install(a => a.On("Deal", DocEvent.BeforeSave, ctx =>
        {
            ctx.Doc["tier"] = Convert.ToDouble(ctx.Doc["amount"]) >= 1000 ? "Gold" : "Standard";
            ctx.Doc["title"] = (ctx.IsNew ? "New: " : "Edited: ") + ctx.Doc["title"];
            return Task.CompletedTask;
        }));

        var small = await DealAsync(5);
        var big = await DealAsync(5000);
        var edited = await _engine.Documents.UpdateAsync("Deal", (string)big["name"]!, Values(("amount", 50)), Ann);

        Assert.Equal("Standard", small["tier"]);
        Assert.Equal("New: Big Deal", small["title"]);
        Assert.Equal("Gold", big["tier"]);
        Assert.Equal("Standard", edited["tier"]); // recalculated from the new amount
        Assert.StartsWith("Edited: ", (string)edited["title"]!);
    }

    [Fact]
    public async Task A_hook_can_refuse_the_change_and_nothing_is_saved()
    {
        Install(a => a.On("Deal", DocEvent.BeforeSave, ctx =>
        {
            if (Convert.ToDouble(ctx.Doc["amount"]) < 0)
                ctx.Refuse("A deal cannot be negative.");
            return Task.CompletedTask;
        }));

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => DealAsync(-1));

        Assert.Equal("A deal cannot be negative.", error.Errors.Single().Message);
        Assert.Empty((await _engine.Documents.ListAsync("Deal", new ListRequest(), TestUsers.Admin)).Data);
    }

    [Fact]
    public async Task What_a_hook_sets_is_checked_like_any_other_input_and_system_fields_stay_theirs()
    {
        Install(a => a.On("Deal", DocEvent.BeforeSave, ctx =>
        {
            ctx.Doc["amount"] = "not a number";
            return Task.CompletedTask;
        }));
        await Assert.ThrowsAsync<DocumentValidationException>(() => DealAsync());

        var other = new TestEngine();
        other.Registry.Add(DocTypeLoader.Parse("""{ "name": "Deal", "autoname": "DL-.####", "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "create": true, "write": true } ] }"""));
        other.AppCatalog.Add(new SalesApp(a => a.On("Deal", DocEvent.BeforeSave, ctx =>
        {
            ctx.Doc["owner"] = "someone-else@example.com";
            ctx.Doc["docstatus"] = 1;
            return Task.CompletedTask;
        })));
        var made = await other.Documents.InsertAsync("Deal", Values(("title", "x")), Ann);
        Assert.Equal("ann@example.com", made["owner"]);
        Assert.Equal(0, Convert.ToInt32(made["docstatus"]));
    }

    [Fact]
    public async Task Hooks_see_submit_cancel_and_delete_and_after_hooks_run_after_the_change_as_the_person()
    {
        Install(a =>
        {
            foreach (var ev in new[] { DocEvent.BeforeSubmit, DocEvent.AfterSubmit, DocEvent.BeforeCancel, DocEvent.AfterCancel, DocEvent.BeforeDelete, DocEvent.AfterDelete, DocEvent.AfterSave })
            {
                var captured = ev;
                a.On("Deal", captured, async ctx => await ctx.Documents.InsertAsync("Audit Note", Values(("text", $"{captured}:{ctx.Name}")), ctx.User, ctx.Cancellation));
            }
        });
        var deal = await DealAsync();
        var name = (string)deal["name"]!;

        await _engine.Documents.SubmitAsync("Deal", name, Ann);
        await _engine.Documents.CancelAsync("Deal", name, Ann);
        var second = (string)(await DealAsync())["name"]!;
        await _engine.Documents.DeleteAsync("Deal", second, Ann);

        var notes = await NotesAsync();
        Assert.Contains($"AfterSave:{name}", notes);
        Assert.Contains($"AfterSubmit:{name}", notes);
        Assert.Contains($"AfterCancel:{name}", notes);
        Assert.Contains($"AfterDelete:{second}", notes);
        // Before events ran too but a hook's own writes do not start hooks, so the notes made while hooks were running add no more.
        Assert.Equal(1, notes.Count(n => n == $"AfterSubmit:{name}"));
    }

    [Fact]
    public async Task A_before_hook_can_stop_a_submit_or_a_delete()
    {
        Install(a =>
        {
            a.On("Deal", DocEvent.BeforeSubmit, ctx => { if (Convert.ToDouble(ctx.Doc["amount"]) > 100) ctx.Refuse("Needs approval."); return Task.CompletedTask; });
            a.On("Deal", DocEvent.BeforeDelete, ctx => { ctx.Refuse("Deals are kept."); return Task.CompletedTask; });
        });
        var big = await DealAsync(500);

        Assert.Equal("Needs approval.", (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Deal", (string)big["name"]!, Ann))).Errors.Single().Message);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.DeleteAsync("Deal", (string)big["name"]!, Ann));
        Assert.NotNull(await _engine.Documents.GetAsync("Deal", (string)big["name"]!, Ann));
    }

    [Fact]
    public async Task A_hook_that_crashes_stops_the_save_but_an_after_hook_that_crashes_does_not_undo_it()
    {
        Install(a =>
        {
            a.On("Deal", DocEvent.BeforeSave, ctx => throw new InvalidOperationException("boom"));
            a.On("Audit Note", DocEvent.AfterSave, ctx => throw new InvalidOperationException("late boom"));
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => DealAsync());
        Assert.Contains("sales", error.Message);

        var note = await _engine.Documents.InsertAsync("Audit Note", Values(("text", "kept")), Ann);
        Assert.NotNull(await _engine.Documents.GetAsync("Audit Note", (string)note["name"]!, Ann));
        var log = (await _engine.Documents.ListAsync("Error Log", new ListRequest { Limit = 10 }, TestUsers.Admin)).Data;
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task Hooks_never_see_passwords_the_person_could_not_set_and_never_start_each_other()
    {
        var seen = new List<string>();
        Install(a =>
        {
            a.On("Audit Note", DocEvent.AfterSave, ctx => { seen.Add("note:" + ctx.Doc["text"]); return Task.CompletedTask; });
            a.On("Deal", DocEvent.AfterSave, async ctx => await ctx.Documents.InsertAsync("Audit Note", Values(("text", "from-hook")), ctx.User, ctx.Cancellation));
        });

        await DealAsync();

        Assert.Empty(seen); // the note the hook made did not start the Audit Note hook
        Assert.Equal(["from-hook"], await NotesAsync());
    }

    [Fact]
    public void Mistakes_in_an_app_are_found_when_it_is_added()
    {
        Assert.Throws<InvalidOperationException>(() => Install(_ => { }, name: "Bad Name"));
        Assert.Throws<InvalidOperationException>(() => Install(_ => { }, name: "qahwa"));
        Assert.Throws<InvalidOperationException>(() => Install(_ => { }, version: ""));
        Install(_ => { });
        Assert.Throws<InvalidOperationException>(() => Install(_ => { })); // twice
        Assert.Throws<InvalidOperationException>(() => Install(a => a.Method("Bad-Name", _ => Task.FromResult<object?>(1)), name: "other"));
        Assert.Throws<InvalidOperationException>(() => Install(a => a.Schedule("fast", TimeSpan.FromSeconds(5), (_, _) => Task.CompletedTask), name: "other2"));
        Assert.Throws<InvalidOperationException>(() => Install(a => { a.Method("x", _ => Task.FromResult<object?>(1)); a.Method("x", _ => Task.FromResult<object?>(2)); }, name: "other3"));
    }

    [Fact]
    public void A_hook_for_a_doctype_that_does_not_exist_is_reported_at_start_up()
    {
        Install(a => a.On("Nothing Like This", DocEvent.AfterSave, _ => Task.CompletedTask));

        Assert.Contains("Nothing Like This", Assert.Throws<InvalidOperationException>(() => _engine.AppCatalog.Validate(_engine.Registry)).Message);
    }

    // ---- methods ----

    private static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task An_app_method_runs_for_people_with_its_role_and_is_refused_for_others()
    {
        Install(a => a.Method("total", async ctx =>
        {
            var page = await ctx.Documents.ListAsync("Deal", new ListRequest { Fields = ["amount"], Limit = 100 }, ctx.User, ctx.Cancellation);
            return page.Data.Sum(r => Convert.ToDouble(r["amount"]));
        }, new MethodOptions { Roles = ["Clerk"] }));
        await DealAsync(10);
        await DealAsync(32);

        Assert.Equal(42.0, await _engine.Methods.InvokeAsync("sales.total", Args(new { }), Ann, false, default));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Methods.InvokeAsync("sales.total", Args(new { }), Vic, false, default));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Methods.InvokeAsync("sales.total", Args(new { }), new UserContext("Guest", []), false, default));
    }

    [Theory]
    [InlineData("sales.nope")]
    [InlineData("nope")]
    [InlineData("../sales.total")]
    [InlineData("sales.total; drop")]
    [InlineData("")]
    public async Task Unknown_or_odd_names_are_not_found(string name)
    {
        Install(a => a.Method("total", _ => Task.FromResult<object?>(1)));

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Methods.InvokeAsync(name, Args(new { }), Ann, false, default));
    }

    [Fact]
    public async Task A_method_is_only_called_with_get_when_it_says_so_and_is_rate_limited()
    {
        Install(a =>
        {
            a.Method("read", _ => Task.FromResult<object?>("ok"), new MethodOptions { AllowGet = true, PerMinute = 3 });
            a.Method("write", _ => Task.FromResult<object?>("done"));
        });

        Assert.Equal("ok", await _engine.Methods.InvokeAsync("sales.read", Args(new { }), Ann, true, default));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Methods.InvokeAsync("sales.write", Args(new { }), Ann, true, default));
        await _engine.Methods.InvokeAsync("sales.read", Args(new { }), Ann, false, default);
        await _engine.Methods.InvokeAsync("sales.read", Args(new { }), Ann, false, default);
        await Assert.ThrowsAsync<RateLimitedException>(() => _engine.Methods.InvokeAsync("sales.read", Args(new { }), Ann, false, default));
    }

    [Fact]
    public async Task The_built_in_functions_count_read_list_set_and_rename_within_the_persons_rights()
    {
        await DealAsync(10, title: "Alpha");
        var beta = await DealAsync(20, title: "Beta");
        var filters = new[] { new object[] { "amount", ">=", 15 } };

        Assert.Equal(2L, await _engine.Methods.InvokeAsync("qahwa.get_count", Args(new { doctype = "Deal" }), Ann, false, default));
        Assert.Equal(1L, await _engine.Methods.InvokeAsync("qahwa.get_count", Args(new { doctype = "Deal", filters }), Ann, false, default));
        Assert.Equal("Beta", await _engine.Methods.InvokeAsync("qahwa.get_value", Args(new { doctype = "Deal", filters, fieldname = "title" }), Ann, false, default));
        var list = (IReadOnlyList<Dictionary<string, object?>>)(await _engine.Methods.InvokeAsync("qahwa.get_list", Args(new { doctype = "Deal", fields = new[] { "title" }, order_by = "title asc" }), Ann, false, default))!;
        Assert.Equal(["Alpha", "Beta"], list.Select(r => (string)r["title"]!));

        await _engine.Methods.InvokeAsync("qahwa.set_value", Args(new { doctype = "Deal", name = beta["name"], fieldname = "tier", value = "Gold" }), Ann, false, default);
        Assert.Equal("Gold", (await _engine.Documents.GetAsync("Deal", (string)beta["name"]!, Ann))["tier"]);

        // The same rights as the ordinary calls: a viewer cannot change anything, nobody reads a DocType they cannot open.
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Methods.InvokeAsync("qahwa.set_value", Args(new { doctype = "Deal", name = beta["name"], fieldname = "tier", value = "x" }), Vic, false, default));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Methods.InvokeAsync("qahwa.get_count", Args(new { doctype = "Audit Note" }), Vic, false, default));
    }

    [Fact]
    public async Task The_built_in_functions_refuse_odd_arguments()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Methods.InvokeAsync("qahwa.get_count", Args(new { }), Ann, false, default));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Methods.InvokeAsync("qahwa.get_count", Args(new { doctype = "Deal", filters = new[] { new object[] { "amount" } } }), Ann, false, default));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Methods.InvokeAsync("qahwa.get_count", Args(new { doctype = "Deal", filters = new[] { new object[] { "amount", "=", new { a = 1 } } } }), Ann, false, default));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Methods.InvokeAsync("qahwa.get_list", Args(new { doctype = "Deal", fields = new[] { "no_such_field" } }), Ann, false, default));
    }

    // ---- install, fixtures, jobs ----

    [Fact]
    public async Task Installing_records_the_app_makes_fixtures_once_and_keeps_the_version_current()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Plain", "autoname": "prompt", "fields": [ { "name": "x", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true } ] }"""));
        Install(a =>
        {
            a.Fixture("Plain", Values(("name", "FIX-1"), ("x", "welcome")));
            a.Fixture("Plain", Values(("name", "FIX-1"), ("x", "welcome again"))); // the same name: left alone
        }, version: "1.0.0");
        await _engine.AppInstaller.InstallAllAsync();
        await _engine.AppInstaller.InstallAllAsync(); // again: nothing more happens

        var rows = (await _engine.Documents.ListAsync("Plain", new ListRequest { Fields = ["x"] }, TestUsers.Admin)).Data;
        Assert.Equal("welcome", Assert.Single(rows)["x"]);
        var apps = await _engine.AppInstaller.ListAsync();
        Assert.Equal("sales", Assert.Single(apps).Name);
        Assert.Equal("1.0.0", apps[0].Version);
    }

    [Fact]
    public async Task A_new_version_is_recorded_without_repeating_fixtures()
    {
        var first = new SalesApp(a => a.Fixture("Audit Note", Values(("text", "once"))), version: "1.0.0");
        _engine.AppCatalog.Add(first);
        await _engine.AppInstaller.InstallAllAsync();

        var catalogV2 = new AppCatalog([new SalesApp(a => a.Fixture("Audit Note", Values(("text", "once"))), version: "1.1.0")]);
        await new AppInstaller(catalogV2, _engine.Registry, _engine.Repository, _engine.Documents, _engine.Clock).InstallAllAsync();

        Assert.Equal(["once"], await NotesAsync());
        Assert.Equal("1.1.0", (await _engine.AppInstaller.ListAsync()).Single().Version);
    }

    [Fact]
    public async Task Scheduled_jobs_run_when_due_and_a_failing_job_only_goes_to_the_error_log()
    {
        var runs = 0;
        Install(a =>
        {
            a.Schedule("tick", TimeSpan.FromMinutes(10), (_, _) => { runs++; return Task.CompletedTask; });
            a.Schedule("bad", TimeSpan.FromMinutes(10), (_, _) => throw new InvalidOperationException("job failed"));
        });

        Assert.Equal(1, await _engine.AppJobs.RunDueAsync());
        Assert.Equal(0, await _engine.AppJobs.RunDueAsync()); // not due yet
        _engine.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, await _engine.AppJobs.RunDueAsync());

        Assert.Equal(2, runs);
        Assert.NotEmpty((await _engine.Documents.ListAsync("Error Log", new ListRequest { Limit = 10 }, TestUsers.Admin)).Data);
    }
}
