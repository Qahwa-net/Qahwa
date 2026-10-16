using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ReportTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Viewer = new("vic@example.com", ["Viewer"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]);

    public ReportTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Sale", "fields": [
                { "name": "customer", "type": "Data", "label": "Customer" }, { "name": "region", "type": "Data" },
                { "name": "amount", "type": "Currency", "label": "Amount" }, { "name": "qty", "type": "Int" },
                { "name": "margin", "type": "Currency", "permLevel": 1 }, { "name": "pin", "type": "Password" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "report": true, "export": true },
                { "role": "Viewer", "read": true },
                { "role": "Boss", "read": true, "permLevel": 1 } ] }
            """));
    }

    private async Task SalesAsync()
    {
        foreach (var (customer, region, amount, qty) in new[] { ("Acme", "North", 100m, 1), ("Acme", "North", 50m, 2), ("Globex", "South", 300m, 3), ("Initech", "North", 10m, 4) })
        {
            _engine.Clock.Advance(TimeSpan.FromSeconds(1));
            await _engine.Documents.InsertAsync("Sale", Values(("customer", customer), ("region", region), ("amount", amount), ("qty", qty)), Ann);
        }
    }

    private static ReportDefinition Listing(params string[] columns) => new() { DocType = "Sale", Columns = columns, Limit = 100 };

    private static ReportAggregate A(string function, string? field = null) => new(function, field);

    private static ReportDefinition Summary(string groupBy, params ReportAggregate[] aggregates) => new() { DocType = "Sale", GroupBy = groupBy, Aggregates = aggregates };

    [Fact]
    public async Task A_list_report_shows_the_chosen_columns_filtered_and_ordered()
    {
        await SalesAsync();
        var definition = Listing("customer", "amount") with
        {
            Filters = [new DashboardFilter("region", "=", JsonDocument.Parse("\"North\"").RootElement)], OrderBy = "amount", Descending = false,
        };

        var run = await _engine.Reports.RunAsync(definition, Ann);

        Assert.Equal(["Customer", "Amount"], run.Columns.Select(c => c.Label));
        Assert.Equal(["Initech", "Acme", "Acme"], run.Rows.Select(r => (string)r["customer"]!));
        Assert.False(run.Grouped);
        Assert.False(run.Truncated);
    }

    [Fact]
    public async Task A_summary_shows_several_numbers_per_group()
    {
        await SalesAsync();

        var run = await _engine.Reports.RunAsync(Summary("region", A("count"), A("sum", "amount"), A("avg", "amount"), A("max", "qty")), Ann);

        Assert.True(run.Grouped);
        Assert.Equal(["region", "count", "sum_amount", "avg_amount", "max_qty"], run.Columns.Select(c => c.Key));
        Assert.Equal("Total of Amount", run.Columns[2].Label);
        var north = run.Rows.Single(r => (string)r["region"]! == "North");
        Assert.Equal(3m, north["count"]);
        Assert.Equal(160m, north["sum_amount"]);
        Assert.Equal(160m / 3, Convert.ToDecimal(north["avg_amount"]));
        Assert.Equal(4m, north["max_qty"]);
        Assert.Equal(300m, run.Rows.Single(r => (string)r["region"]! == "South")["sum_amount"]);
    }

    [Fact]
    public async Task Saved_reports_are_run_for_whoever_opens_them_with_their_own_rights()
    {
        await SalesAsync();
        var saved = await _engine.Reports.CreateAsync(new ReportRequest("Sales by region", true, Summary("region", A("count"))), Ann);

        Assert.Equal(2, (await _engine.Reports.RunAsync(saved.Id, Bob)).Rows.Count);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Reports.RunAsync(saved.Id, Viewer)); // may read but has no report right
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Reports.RunAsync(saved.Id, Eve)); // may not even read
    }

    [Fact]
    public async Task A_list_report_never_shows_fields_the_viewer_may_not_read()
    {
        await SalesAsync();
        var saved = await _engine.Reports.CreateAsync(new ReportRequest("With margin", true, Listing("customer", "margin")), Ann);

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reports.RunAsync(saved.Id, Ann)); // refused with a reason, never quietly shown
        Assert.Contains(error.Errors, e => e.Message.Contains("margin", StringComparison.OrdinalIgnoreCase) || e.Field.Contains("margin", StringComparison.OrdinalIgnoreCase) || e.Field == "fields");
    }

    [Fact]
    public async Task Private_reports_are_only_for_their_owner_and_public_ones_for_everyone_signed_in()
    {
        var mine = await _engine.Reports.CreateAsync(new ReportRequest("Private", false, Listing("customer")), Ann);
        var open = await _engine.Reports.CreateAsync(new ReportRequest("Open", true, Listing("customer")), Ann);

        Assert.Equal(["Open", "Private"], (await _engine.Reports.ListAsync(Ann)).Select(r => r.Title));
        Assert.Equal(["Open"], (await _engine.Reports.ListAsync(Bob)).Select(r => r.Title));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Reports.GetAsync(mine.Id, Bob));
        Assert.False((await _engine.Reports.GetAsync(open.Id, Bob)).CanEdit);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Reports.UpdateAsync(open.Id, new ReportRequest("Hijacked", true, Listing("customer")), null, Bob));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Reports.DeleteAsync(open.Id, Bob));
        Assert.Equal(2, (await _engine.Reports.ListAsync(TestUsers.SystemManager)).Count); // a manager sees all
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Reports.ListAsync(UserContext.Anonymous));
    }

    [Fact]
    public async Task The_owner_changes_and_deletes_and_a_stale_edit_is_refused()
    {
        var saved = await _engine.Reports.CreateAsync(new ReportRequest("First", false, Listing("customer")), Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));

        var changed = await _engine.Reports.UpdateAsync(saved.Id, new ReportRequest("Second", true, Listing("customer", "amount")), saved.Modified, Ann);

        Assert.Equal("Second", changed.Title);
        Assert.True(changed.IsPublic);
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Reports.UpdateAsync(saved.Id, new ReportRequest("Third", true, Listing("customer")), saved.Modified, Ann));
        await _engine.Reports.DeleteAsync(saved.Id, Ann);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Reports.GetAsync(saved.Id, Ann));
    }

    [Fact]
    public async Task Bad_definitions_are_refused_with_the_field_that_is_wrong()
    {
        async Task<DocumentValidationException> Refused(ReportDefinition? definition) =>
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reports.CreateAsync(new ReportRequest("x", false, definition), Ann));

        Assert.Contains((await Refused(null)).Errors, e => e.Field == "definition");
        Assert.Contains((await Refused(Listing("customer") with { DocType = "Nowhere" })).Errors, e => e.Field == "docType");
        Assert.Contains((await Refused(Listing())).Errors, e => e.Field == "columns");
        Assert.Contains((await Refused(Listing("pin"))).Errors, e => e.Field == "columns"); // a password
        Assert.Contains((await Refused(Listing("nope"))).Errors, e => e.Field == "columns");
        Assert.Contains((await Refused(Listing("customer") with { Limit = 5000 })).Errors, e => e.Field == "limit");
        Assert.Contains((await Refused(Listing("customer") with { OrderBy = "nope" })).Errors, e => e.Field == "orderBy");
        Assert.Contains((await Refused(Listing("customer") with { Filters = [new DashboardFilter("nope", "=", null)] })).Errors, e => e.Field == "filters[0].field");
        Assert.Contains((await Refused(Listing("customer") with { Filters = [new DashboardFilter("region", "; drop table x", null)] })).Errors, e => e.Field == "filters[0].op");
        Assert.Contains((await Refused(Summary("pin", A("count")))).Errors, e => e.Field == "groupBy");
        Assert.Contains((await Refused(Summary("region"))).Errors, e => e.Field == "aggregates");
        Assert.Contains((await Refused(Summary("region", A("sum", "customer")))).Errors, e => e.Field == "aggregates[0].field"); // not numeric
        Assert.Contains((await Refused(Summary("region", A("median", "amount")))).Errors, e => e.Field == "aggregates[0].function");
        Assert.Contains((await Refused(Summary("region", A("count"), A("count")))).Errors, e => e.Field == "aggregates");
        Assert.Contains((await Refused(Summary("region", Enumerable.Range(0, 6).Select(i => new ReportAggregate("sum", i % 2 == 0 ? "amount" : "qty")).ToArray()))).Errors, e => e.Field == "aggregates");
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reports.CreateAsync(new ReportRequest("", false, Listing("customer")), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reports.CreateAsync(new ReportRequest(new string('x', 101), false, Listing("customer")), Ann));
    }

    [Fact]
    public async Task Hostile_filter_values_are_just_values()
    {
        await SalesAsync();
        var hostile = Listing("customer") with { Filters = [new DashboardFilter("customer", "=", JsonDocument.Parse("\"x'; DROP TABLE \\\"tabSale\\\"; --\"").RootElement)] };

        var run = await _engine.Reports.RunAsync(hostile, Ann);

        Assert.Empty(run.Rows);
        Assert.Equal(4, (await _engine.Reports.RunAsync(Listing("customer"), Ann)).Rows.Count); // the table is fine
    }

    [Fact]
    public async Task A_download_uses_the_labels_and_needs_the_export_right_for_a_list()
    {
        await SalesAsync();
        var list = await _engine.Reports.CreateAsync(new ReportRequest("List", true, Listing("customer", "amount")), Ann);
        var summary = await _engine.Reports.CreateAsync(new ReportRequest("Summary", true, Summary("region", A("count"), A("sum", "amount"))), Ann);

        var (columns, rows, truncated, title) = await _engine.Reports.ExportAsync(list.Id, Ann);
        Assert.Equal(["Customer", "Amount"], columns);
        Assert.Equal(4, rows.Count);
        Assert.False(truncated);
        Assert.Equal("List", title);

        var (summaryColumns, _, _, _) = await _engine.Reports.ExportAsync(summary.Id, Ann);
        Assert.Equal(["Region", "Count", "Total of Amount"], summaryColumns.Select(c => c == "region" ? "Region" : c));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Reports.ExportAsync(list.Id, Viewer));
    }

    [Fact]
    public async Task A_definition_that_no_longer_fits_its_doctype_fails_with_a_reason_when_opened()
    {
        var saved = await _engine.Reports.CreateAsync(new ReportRequest("Old", true, Listing("customer", "qty")), Ann);
        await _engine.Repository.UpdateAsync(_engine.Registry.Get("Report"), saved.Id, Values(("definition", """{ "docType": "Sale", "columns": ["gone_field"], "limit": 10 }""")), null);

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reports.RunAsync(saved.Id, Ann));

        Assert.Contains(error.Errors, e => e.Field == "columns");
    }
}
