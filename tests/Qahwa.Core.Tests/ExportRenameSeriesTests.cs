using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Export and grouped reports, rename and merge, naming series.</summary>
public class ExportRenameSeriesTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Analyst = new("analyst@example.com", ["Analyst"]);
    private static readonly UserContext Reader = new("reader@example.com", ["Reader"]);
    private static readonly UserContext Editor = new("editor@example.com", ["Editor"]);

    public ExportRenameSeriesTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Sale", "autoname": "SALE-.####",
              "fields": [
                { "name": "customer", "type": "Data" }, { "name": "amount", "type": "Currency" },
                { "name": "secret", "type": "Password" }, { "name": "cost", "type": "Currency", "permLevel": 1 }
              ],
              "permissions": [
                { "role": "Analyst", "read": true, "create": true, "export": true, "report": true },
                { "role": "Reader", "read": true }
              ]
            }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Team Member", "isChildTable": true, "fields": [ { "name": "person", "type": "Data" } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Team", "autoname": "field:team_name", "allowRename": true,
              "fields": [ { "name": "team_name", "type": "Data", "required": true }, { "name": "members", "type": "Table", "options": "Team Member" } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true, "delete": true }, { "role": "Reader", "read": true, "write": true } ]
            }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Work Item", "autoname": "WI-.####",
              "fields": [ { "name": "title", "type": "Data" }, { "name": "team", "type": "Link", "options": "Team" } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true } ]
            }
            """));
    }

    // ---- export and report ----

    private async Task SalesAsync()
    {
        foreach (var (customer, amount) in new[] { ("Acme", 10), ("Acme", 30), ("Globex", 5) })
            await _engine.Documents.InsertAsync("Sale", Values(("customer", customer), ("amount", amount)), Analyst);
    }

    [Fact]
    public async Task Export_needs_the_export_right_as_well_as_read()
    {
        await SalesAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ExportAsync("Sale", new ListRequest(), Reader));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ExportAsync("Sale", new ListRequest(), TestUsers.Nobody));

        var export = await _engine.Documents.ExportAsync("Sale", new ListRequest(), Analyst);
        Assert.Equal(3, export.Rows.Count);
        Assert.False(export.Truncated);
    }

    [Fact]
    public async Task Export_leaves_out_passwords_and_fields_the_user_may_not_read()
    {
        await SalesAsync();

        var export = await _engine.Documents.ExportAsync("Sale", new ListRequest(), Analyst);

        Assert.DoesNotContain("secret", export.Columns);
        Assert.DoesNotContain("cost", export.Columns); // permission level 1
        Assert.Contains("amount", export.Columns);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ExportAsync("Sale", new ListRequest { Fields = ["secret"] }, TestUsers.Admin));
        await Assert.ThrowsAnyAsync<Exception>(() => _engine.Documents.ExportAsync("Sale", new ListRequest { Fields = ["cost"] }, Analyst));
    }

    [Fact]
    public async Task Export_reads_more_than_one_page_and_honours_filters()
    {
        for (var i = 0; i < 1005; i++)
            await _engine.Documents.InsertAsync("Sale", Values(("customer", i % 2 == 0 ? "Even" : "Odd"), ("amount", 1)), Analyst);

        var all = await _engine.Documents.ExportAsync("Sale", new ListRequest(), Analyst);
        var even = await _engine.Documents.ExportAsync("Sale", new ListRequest { Filters = [new("customer", "=", "Even")] }, Analyst);

        Assert.Equal(1005, all.Rows.Count);
        Assert.Equal(503, even.Rows.Count);
    }

    [Fact]
    public async Task A_report_groups_and_summarises()
    {
        await SalesAsync();

        var sum = await _engine.Documents.ReportAsync("Sale", "customer", "sum", "amount", new ListRequest(), Analyst);
        var count = await _engine.Documents.ReportAsync("Sale", "customer", "count", null, new ListRequest(), Analyst);
        var avg = await _engine.Documents.ReportAsync("Sale", "customer", "avg", "amount", new ListRequest(), Analyst);

        Assert.Equal([("Acme", 40m), ("Globex", 5m)], sum.Rows.Select(r => (r.Group!, r.Value)));
        Assert.Equal([("Acme", 2m), ("Globex", 1m)], count.Rows.Select(r => (r.Group!, r.Value)));
        Assert.Equal(20m, avg.Rows[0].Value);
    }

    [Fact]
    public async Task Reports_need_the_right_and_validate_what_they_are_given()
    {
        await SalesAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ReportAsync("Sale", "customer", "count", null, new ListRequest(), Reader));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ReportAsync("Sale", "customer", "median", "amount", new ListRequest(), Analyst));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ReportAsync("Sale", "nope", "count", null, new ListRequest(), Analyst));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ReportAsync("Sale", "secret", "count", null, new ListRequest(), Analyst));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ReportAsync("Sale", "customer", "sum", "customer", new ListRequest(), Analyst));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ReportAsync("Sale", "customer", "sum", "x\"; DROP TABLE tabSale;--", new ListRequest(), Analyst));
    }

    // ---- rename and merge ----

    private async Task TeamsAsync()
    {
        await _engine.Documents.InsertAsync("Team", Values(("team_name", "Alpha"), ("members", new[] { Values(("person", "Ann")) })), Editor);
        await _engine.Documents.InsertAsync("Team", Values(("team_name", "Beta"), ("members", new[] { Values(("person", "Bob")) })), Editor);
    }

    [Fact]
    public async Task Renaming_moves_the_document_its_rows_and_every_link_to_it()
    {
        await TeamsAsync();
        var item = (string)(await _engine.Documents.InsertAsync("Work Item", Values(("title", "T"), ("team", "Alpha")), Editor))["name"]!;

        var renamed = await _engine.Documents.RenameAsync("Team", "Alpha", "Alpha Prime", false, Editor);

        Assert.Equal("Alpha Prime", renamed);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("Team", "Alpha", Editor));
        var team = await _engine.Documents.GetAsync("Team", "Alpha Prime", Editor);
        Assert.Equal("Ann", ((IEnumerable<Dictionary<string, object?>>)team["members"]!).Single()["person"]);
        Assert.Equal("Alpha Prime", (await _engine.Documents.GetAsync("Work Item", item, Editor))["team"]);
    }

    [Fact]
    public async Task Renaming_onto_an_existing_name_needs_an_explicit_merge()
    {
        await TeamsAsync();

        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.RenameAsync("Team", "Alpha", "Beta", false, Editor));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.RenameAsync("Team", "Alpha", "Nowhere", true, Editor));
    }

    [Fact]
    public async Task Merging_repoints_links_and_removes_the_old_document()
    {
        await TeamsAsync();
        var item = (string)(await _engine.Documents.InsertAsync("Work Item", Values(("title", "T"), ("team", "Alpha")), Editor))["name"]!;

        await _engine.Documents.RenameAsync("Team", "Alpha", "Beta", true, Editor);

        Assert.Equal("Beta", (await _engine.Documents.GetAsync("Work Item", item, Editor))["team"]);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("Team", "Alpha", Editor));
        var members = (IEnumerable<Dictionary<string, object?>>)(await _engine.Documents.GetAsync("Team", "Beta", Editor))["members"]!;
        Assert.Equal(["Bob"], members.Select(m => m["person"]));
    }

    [Fact]
    public async Task Rename_checks_who_may_do_it_and_what_name_is_given()
    {
        await TeamsAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.RenameAsync("Team", "Alpha", "X", false, TestUsers.Nobody));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.RenameAsync("Team", "Alpha", "Beta", true, Reader)); // writes, but may not delete
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.RenameAsync("Team", "Alpha", "  ", false, Editor));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.RenameAsync("Team", "Alpha", "bad\nname", false, Editor));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.RenameAsync("Team", "Alpha", new string('x', 141), false, Editor));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.RenameAsync("Team", "Ghost", "X", false, Editor));
        Assert.Equal("Alpha", await _engine.Documents.RenameAsync("Team", "Alpha", "Alpha", false, Editor)); // same name: nothing to do

        // Work Item does not allow renaming at all.
        var item = (string)(await _engine.Documents.InsertAsync("Work Item", Values(("title", "T")), Editor))["name"]!;
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.RenameAsync("Work Item", item, "Z", false, Editor));
    }

    [Fact]
    public async Task Rename_follows_shares_and_user_permissions()
    {
        await TeamsAsync();
        await _engine.Documents.InsertAsync("User", Values(("email", "p@example.com"), ("full_name", "P")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User Permission", Values(("user", "p@example.com"), ("allow", "Team"), ("for_value", "Alpha"), ("apply_to_all_doctypes", true)), TestUsers.Admin);
        await _engine.ShareManager.SetAsync("Team", "Alpha", "p@example.com", true, false, Editor);

        await _engine.Documents.RenameAsync("Team", "Alpha", "Omega", false, Editor);

        var permission = (await _engine.Documents.ListAsync("User Permission", new ListRequest(), TestUsers.Admin)).Data.Single();
        Assert.Equal("Omega", permission["for_value"]);
        Assert.Equal("Omega", Assert.Single(await _engine.Shares.RowsForDocumentAsync("Team", "Omega"))["share_name"]);
    }

    // ---- naming series ----

    [Fact]
    public async Task The_series_list_shows_prefixes_digits_counters_and_users_of_each_series()
    {
        await _engine.Documents.InsertAsync("Sale", Values(("customer", "A")), Analyst);

        var sale = (await _engine.NamingSeries.ListAsync(TestUsers.SystemManager)).Single(s => s.Prefix == "SALE-");

        Assert.Equal(4, sale.Digits);
        Assert.Equal(1, sale.Current);
        Assert.Equal(["Sale"], sale.DocTypes);
    }

    [Fact]
    public async Task Setting_the_counter_changes_the_next_name()
    {
        await _engine.NamingSeries.SetAsync("SALE-", 99, TestUsers.SystemManager);

        var sale = await _engine.Documents.InsertAsync("Sale", Values(("customer", "A")), Analyst);

        Assert.Equal("SALE-0100", sale["name"]);
    }

    [Fact]
    public async Task The_series_manager_is_for_system_managers_and_validates_input()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.NamingSeries.ListAsync(Editor));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.NamingSeries.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.NamingSeries.SetAsync("SALE-", 5, Editor));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.NamingSeries.SetAsync("MADE-UP-", 5, TestUsers.SystemManager));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.NamingSeries.SetAsync("SALE-", -1, TestUsers.SystemManager));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.NamingSeries.SetAsync("SALE-", NamingSeriesManager.MaxCounter + 1, TestUsers.SystemManager));
    }
}
