using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DashboardTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Intern = new("ivy@example.com", ["Intern"]); // reads, no report right
    private static readonly UserContext Rep = new("rex@example.com", ["Rep"]); // reads and reports only on own documents

    public DashboardTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Sale", "fields": [
                { "name": "customer", "type": "Data" }, { "name": "region", "type": "Data" }, { "name": "amount", "type": "Currency" },
                { "name": "sold_on", "type": "Date" }, { "name": "status", "type": "Data" }, { "name": "secret", "type": "Password" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "report": true },
                { "role": "Intern", "read": true },
                { "role": "Rep", "read": true, "create": true, "report": true, "ifOwner": true } ] }
            """));
    }

    private async Task SaleAsync(UserContext by, string customer, string region, decimal amount, string date, string status = "Open")
    {
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.InsertAsync("Sale", Values(("customer", customer), ("region", region), ("amount", amount), ("sold_on", date), ("status", status)), by);
    }

    private async Task SeedAsync()
    {
        await SaleAsync(Ann, "Acme", "North", 100, "2026-01-05");
        await SaleAsync(Ann, "Acme", "North", 300, "2026-01-20");
        await SaleAsync(Ann, "Bolt", "South", 50, "2026-02-03", "Won");
        await SaleAsync(Bob, "Cyan", "South", 150, "2026-03-09", "Won");
    }

    private static JsonElement V(object value) => JsonSerializer.SerializeToElement(value);

    private static DashboardItem Number(string function = "count", string? field = null, params DashboardFilter[] filters) =>
        new() { Type = DashboardItem.Number, DocType = "Sale", Title = "N", Function = function, ValueField = field, Filters = filters };

    private static DashboardItem Chart(string groupBy, string function = "count", string? field = null, string? period = null, string type = "bar") =>
        new() { Type = DashboardItem.Chart, DocType = "Sale", Title = "C", GroupBy = groupBy, Function = function, ValueField = field, Period = period, ChartType = type };

    private async Task<DashboardResult> OneAsync(DashboardItem item, UserContext viewer)
    {
        var info = await _engine.Dashboards.CreateAsync(new DashboardRequest("T", true, [item]), TestUsers.Admin);
        return Assert.Single(await _engine.Dashboards.DataAsync(info.Id, viewer));
    }

    // ---- tiles ----

    [Fact]
    public async Task A_number_tile_counts_and_summarises_with_filters()
    {
        await SeedAsync();
        Assert.Equal(4m, (await _engine.Dashboards.PreviewAsync(Number(), Ann)).Number);
        Assert.Equal(600m, (await _engine.Dashboards.PreviewAsync(Number("sum", "amount"), Ann)).Number);
        Assert.Equal(150m, (await _engine.Dashboards.PreviewAsync(Number("avg", "amount"), Ann)).Number);
        Assert.Equal(50m, (await _engine.Dashboards.PreviewAsync(Number("min", "amount"), Ann)).Number);
        Assert.Equal(300m, (await _engine.Dashboards.PreviewAsync(Number("max", "amount"), Ann)).Number);
        Assert.Equal(2m, (await _engine.Dashboards.PreviewAsync(Number("count", null, new DashboardFilter("status", "=", V("Won"))), Ann)).Number);
        Assert.Equal(450m, (await _engine.Dashboards.PreviewAsync(Number("sum", "amount", new DashboardFilter("region", "=", V("North")), new DashboardFilter("amount", ">", V(50))), Ann)).Number - 0m + 50m);
    }

    [Fact]
    public async Task A_chart_groups_by_a_field_biggest_first_and_by_a_period_in_date_order()
    {
        await SeedAsync();

        var byRegion = (await _engine.Dashboards.PreviewAsync(Chart("region", "sum", "amount"), Ann)).Points!;
        Assert.Equal([("North", 400m), ("South", 200m)], byRegion.Select(p => (p.Label, p.Value)));

        var byMonth = (await _engine.Dashboards.PreviewAsync(Chart("sold_on", "count", null, "month", "line"), Ann)).Points!;
        Assert.Equal([("2026-01", 2m), ("2026-02", 1m), ("2026-03", 1m)], byMonth.Select(p => (p.Label, p.Value)));

        var byYear = (await _engine.Dashboards.PreviewAsync(Chart("sold_on", "sum", "amount", "year"), Ann)).Points!;
        Assert.Equal([("2026", 600m)], byYear.Select(p => (p.Label, p.Value)));

        var average = (await _engine.Dashboards.PreviewAsync(Chart("sold_on", "avg", "amount", "month"), Ann)).Points!;
        Assert.Equal(200m, average[0].Value); // the average of the documents of January, not of two averages
    }

    [Fact]
    public async Task A_chart_shows_at_most_thirty_points()
    {
        for (var i = 0; i < 35; i++)
            await SaleAsync(Ann, $"C{i:00}", $"R{i:00}", i + 1, "2026-01-01");

        var result = await _engine.Dashboards.PreviewAsync(Chart("region", "sum", "amount"), Ann);

        Assert.Equal(30, result.Points!.Count);
        Assert.True(result.Truncated);
        Assert.Equal(35m, result.Points[0].Value); // the biggest are kept
    }

    [Fact]
    public async Task A_list_tile_shows_chosen_columns_in_order()
    {
        await SeedAsync();
        var item = new DashboardItem { Type = DashboardItem.List, DocType = "Sale", Columns = ["customer", "amount"], OrderBy = "amount", Descending = true, Limit = 2 };

        var result = await _engine.Dashboards.PreviewAsync(item, Ann);

        Assert.Equal(["customer", "amount"], result.Columns);
        Assert.Equal([300m, 150m], result.Rows!.Select(r => Convert.ToDecimal(r["amount"])));
        Assert.True(result.Truncated);
    }

    // ---- permissions ----

    [Fact]
    public async Task Every_tile_uses_the_viewers_permissions_and_one_tile_cannot_spoil_the_others()
    {
        await SeedAsync();
        var info = await _engine.Dashboards.CreateAsync(new DashboardRequest("Sales", true, [
            Number(), new DashboardItem { Type = DashboardItem.List, DocType = "Sale", Columns = ["customer"] }]), Ann);

        var intern = await _engine.Dashboards.DataAsync(info.Id, Intern);
        Assert.Contains("do not have access", intern[0].Error);
        Assert.Null(intern[0].Number);
        Assert.Equal(4, intern[1].Rows!.Count); // reading is enough for a list

        var outsider = await _engine.Dashboards.DataAsync(info.Id, new UserContext("eve@example.com", ["Outsider"]));
        Assert.All(outsider, r => Assert.NotNull(r.Error));
    }

    [Fact]
    public async Task Someone_limited_to_their_own_documents_only_counts_their_own()
    {
        await SeedAsync();
        await SaleAsync(Rep, "Own", "North", 10, "2026-01-01");

        Assert.Equal(1m, (await _engine.Dashboards.PreviewAsync(Number(), Rep)).Number);
        Assert.Equal(5m, (await _engine.Dashboards.PreviewAsync(Number(), Ann)).Number);
    }

    [Fact]
    public async Task Password_fields_cannot_be_charted_summed_or_listed()
    {
        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.PreviewAsync(Chart("secret"), Ann))).Errors.Select(e => e.Field);
        Assert.Contains("groupBy", errors);
        var list = new DashboardItem { Type = DashboardItem.List, DocType = "Sale", Columns = ["secret"] };
        Assert.Contains("columns", (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.PreviewAsync(list, Ann))).Errors.Select(e => e.Field));
    }

    // ---- who sees and changes a dashboard ----

    [Fact]
    public async Task A_private_dashboard_is_only_for_its_owner_and_a_public_one_for_everyone()
    {
        var mine = await _engine.Dashboards.CreateAsync(new DashboardRequest("Mine", false, [Number()]), Ann);
        var shared = await _engine.Dashboards.CreateAsync(new DashboardRequest("Shared", true, [Number()]), Ann);

        Assert.Equal(["Mine", "Shared"], (await _engine.Dashboards.ListAsync(Ann)).Select(d => d.Title).Order());
        Assert.Equal(["Shared"], (await _engine.Dashboards.ListAsync(Bob)).Select(d => d.Title));
        Assert.Equal(2, (await _engine.Dashboards.ListAsync(TestUsers.SystemManager)).Count);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Dashboards.GetAsync(mine.Id, Bob));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Dashboards.DataAsync(mine.Id, Bob));

        var detail = await _engine.Dashboards.GetAsync(shared.Id, Bob);
        Assert.False(detail.CanEdit);
        Assert.True((await _engine.Dashboards.GetAsync(shared.Id, Ann)).CanEdit);
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Dashboards.ListAsync(UserContext.Anonymous));
    }

    [Fact]
    public async Task Only_the_owner_or_a_system_manager_changes_or_deletes_a_dashboard()
    {
        var shared = await _engine.Dashboards.CreateAsync(new DashboardRequest("Shared", true, [Number()]), Ann);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Dashboards.UpdateAsync(shared.Id, new DashboardRequest("Hacked", true, []), null, Bob));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Dashboards.DeleteAsync(shared.Id, Bob));

        var updated = await _engine.Dashboards.UpdateAsync(shared.Id, new DashboardRequest("Renamed", false, [Number(), Number("sum", "amount")]), shared.Modified, Ann);
        Assert.Equal(("Renamed", false, 2), (updated.Title, updated.IsPublic, updated.Items));
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Dashboards.UpdateAsync(shared.Id, new DashboardRequest("Late", false, []), shared.Modified, Ann));

        await _engine.Dashboards.DeleteAsync(shared.Id, TestUsers.SystemManager);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Dashboards.GetAsync(shared.Id, Ann));
    }

    [Fact]
    public async Task A_person_may_have_fifty_dashboards()
    {
        for (var i = 0; i < DashboardService.MaxPerUser; i++)
            await _engine.Dashboards.CreateAsync(new DashboardRequest($"D{i}", false, []), Ann);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.CreateAsync(new DashboardRequest("One more", false, []), Ann));
        await _engine.Dashboards.CreateAsync(new DashboardRequest("Bob's", false, []), Bob);
    }

    // ---- what is accepted ----

    [Fact]
    public async Task Problems_are_reported_by_tile_and_field()
    {
        var items = new[]
        {
            new DashboardItem { Type = "Table", DocType = "Sale" },
            Number() with { DocType = "Nope" },
            Number("sum", "customer"),
            Number("median"),
            Chart("nope"),
            Chart("region", period: "week"),
            Chart("sold_on", period: "week"),
            Chart("region", type: "radar"),
            new DashboardItem { Type = DashboardItem.List, DocType = "Sale", Columns = [], Limit = 500 },
            Number() with { Color = "pink", Title = "bad\u0001" },
            Number("count", null, new DashboardFilter("nope", "=", V(1))),
            Number("count", null, new DashboardFilter("status", "", V(1))),
        };

        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.CreateAsync(new DashboardRequest("T", false, items), Ann))).Errors.Select(e => e.Field).ToHashSet();

        foreach (var expected in new[]
        {
            "items[0].type", "items[1].docType", "items[2].valueField", "items[3].function", "items[4].groupBy", "items[5].period", "items[6].period",
            "items[7].chartType", "items[8].columns", "items[8].limit", "items[9].color", "items[9].title", "items[10].filters[0].field", "items[11].filters[0].op",
        })
            Assert.Contains(expected, errors);
    }

    [Fact]
    public async Task Titles_counts_and_hostile_values_are_checked_and_tiles_get_unique_ids()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.CreateAsync(new DashboardRequest("", false, []), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.CreateAsync(new DashboardRequest("T", false, Enumerable.Range(0, 25).Select(_ => Number()).ToList()), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.CreateAsync(new DashboardRequest("T", false,
            [Number("count", null, new DashboardFilter("status", "=", V(new { a = 1 })))]), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Dashboards.CreateAsync(new DashboardRequest("T", false,
            [Number("count", null, new DashboardFilter("status; DROP TABLE x; --", "=", V(1)))]), Ann));

        var info = await _engine.Dashboards.CreateAsync(new DashboardRequest("T", false, [Number() with { Id = "same" }, Number() with { Id = "same" }, Number()]), Ann);
        var ids = (await _engine.Dashboards.GetAsync(info.Id, Ann)).Items.Select(i => i.Id).ToList();
        Assert.Equal(3, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotEmpty(id));
    }

    [Fact]
    public async Task A_filter_with_a_bad_operator_gives_a_tile_error_not_a_failure()
    {
        await SeedAsync();
        var result = await OneAsync(Number("count", null, new DashboardFilter("status", "regexp", V("x"))), Ann);

        Assert.NotNull(result.Error);
    }
}
