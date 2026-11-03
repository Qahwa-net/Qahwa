using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresPreparedReportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_prepared_report_is_built_once_with_all_its_rows_even_when_servers_race()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Sale", "fields": [ { "name": "customer", "type": "Data" }, { "name": "amount", "type": "Currency" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true, "export": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk", UserContext.AllRole]);
        await db.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await db.Documents.InsertAsync("User", Values(("email", clerk.User), ("full_name", "Clerk"), ("enabled", true)), TestUsers.Admin);
        await db.Documents.InsertAsync("Has Role", Values(("user", clerk.User), ("role", "Clerk")), TestUsers.Admin);
        for (var i = 0; i < 1200; i++) // more than a live run shows
            await db.Documents.InsertAsync("Sale", Values(("customer", $"Customer {i}"), ("amount", i)), clerk);
        var report = (await db.Reports.CreateAsync(new ReportRequest("All sales", false,
            new ReportDefinition { DocType = "Sale", Columns = ["customer", "amount"], Limit = 50 }), clerk)).Id;

        var queued = await db.PreparedReports.RequestAsync(report, clerk);
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.PreparedReports.RunDueAsync()));

        Assert.Equal(1, runs.Sum(r => r.Done));
        var result = await db.PreparedReports.GetAsync(queued.Id, clerk);
        Assert.Equal("Done", result.Info.Status);
        Assert.Equal(1200, result.Rows.Count); // all of them, not the live run's cap or the report's own limit
        Assert.Equal(["customer", "amount"], result.Columns); // the fields have no labels, so the header is the field name
    }
}
