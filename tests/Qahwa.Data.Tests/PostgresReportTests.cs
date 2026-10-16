using System.Text.Json;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresReportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_saved_report_runs_grouped_and_listed_on_postgres_with_the_viewers_rights()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Sale", "fields": [ { "name": "region", "type": "Data" }, { "name": "amount", "type": "Currency" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true }, { "role": "Viewer", "read": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        foreach (var (region, amount) in new[] { ("North", 10m), ("North", 20.5m), ("South", 5m) })
            await db.Documents.InsertAsync("Sale", Values(("region", region), ("amount", amount)), clerk);

        var saved = await db.Reports.CreateAsync(new ReportRequest("By region", true, new ReportDefinition
        {
            DocType = "Sale", GroupBy = "region", Aggregates = [new ReportAggregate("count"), new ReportAggregate("sum", "amount"), new ReportAggregate("avg", "amount")],
        }), clerk);
        var run = await db.Reports.RunAsync(saved.Id, clerk);
        var north = run.Rows.Single(r => (string)r["region"]! == "North");

        Assert.Equal(2m, north["count"]);
        Assert.Equal(30.5m, north["sum_amount"]);
        Assert.Equal(15.25m, north["avg_amount"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => db.Reports.RunAsync(saved.Id, new UserContext("v@example.com", ["Viewer"])));

        var list = await db.Reports.RunAsync(new ReportDefinition
        {
            DocType = "Sale", Columns = ["region", "amount"], OrderBy = "amount", Descending = false,
            Filters = [new DashboardFilter("region", "=", JsonDocument.Parse("\"North\"").RootElement)],
        }, clerk);
        Assert.Equal([10m, 20.5m], list.Rows.Select(r => Convert.ToDecimal(r["amount"])));
    }
}
