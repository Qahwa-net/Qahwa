using System.Text.Json;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresDashboardTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_dashboard_is_stored_and_its_tiles_are_worked_out_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Invoice Line", "fields": [ { "name": "region", "type": "Data" }, { "name": "amount", "type": "Currency" }, { "name": "billed_on", "type": "Date" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        foreach (var (region, amount, date) in new[] { ("North", 100m, "2026-01-05"), ("North", 300m, "2026-01-20"), ("South", 50m, "2026-02-03") })
            await db.Documents.InsertAsync("Invoice Line", Values(("region", region), ("amount", amount), ("billed_on", date)), clerk);

        var info = await db.Dashboards.CreateAsync(new DashboardRequest("Billing", true,
        [
            new DashboardItem { Type = DashboardItem.Number, DocType = "Invoice Line", Function = "sum", ValueField = "amount", Filters = [new DashboardFilter("region", "=", JsonSerializer.SerializeToElement("North"))] },
            new DashboardItem { Type = DashboardItem.Chart, DocType = "Invoice Line", GroupBy = "billed_on", Period = "month", Function = "sum", ValueField = "amount" },
        ]), clerk);

        var data = await db.Dashboards.DataAsync(info.Id, clerk);

        Assert.Equal(400m, data[0].Number);
        Assert.Equal([("2026-01", 400m), ("2026-02", 50m)], data[1].Points!.Select(p => (p.Label, p.Value)));
        Assert.Single(await db.Dashboards.ListAsync(clerk));
        await db.Dashboards.DeleteAsync(info.Id, clerk);
        Assert.Empty(await db.Dashboards.ListAsync(clerk));
    }
}
