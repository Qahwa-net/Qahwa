using Qahwa.Core;
using Qahwa.Data;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>Runs the schema engine and document engine against a real PostgreSQL (needs Docker).</summary>
public class PostgresIntegrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        return await postgres.CreateStackAsync();
    }

    [SkippableFact]
    public async Task Migration_is_idempotent()
    {
        await using var db = await StackAsync();

        foreach (var meta in db.Registry.All)
            Assert.True((await db.Sync.PlanAsync(meta)).IsEmpty, $"{meta.Name} should be up to date after migrate");
    }

    [SkippableFact]
    public async Task Adding_a_field_adds_a_column_and_keeps_existing_rows()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Territory", Values(("territory_name", "Riyadh")), TestUsers.Manager);

        var extended = DocTypeLoader.Parse("""
            { "name": "Territory", "autoname": "field:territory_name",
              "fields": [ { "name": "territory_name", "type": "Data", "required": true },
                          { "name": "region", "type": "Data" } ] }
            """);
        var plan = await db.Sync.PlanAsync(extended);
        await db.Sync.ApplyAsync(plan);

        Assert.Contains(plan.Statements, s => s.Sql.Contains("ADD") && s.Sql.Contains("region"));
        Assert.True((await db.Sync.PlanAsync(extended)).IsEmpty);
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabTerritory\""));
    }

    [SkippableFact]
    public async Task Type_drift_is_reported_but_not_applied()
    {
        await using var db = await StackAsync();
        await using (var command = db.DataSource.CreateCommand(
            db.IsPostgres ? "ALTER TABLE \"tabCustomer\" ALTER COLUMN \"credit_limit\" TYPE text" : "ALTER TABLE [tabCustomer] ALTER COLUMN [credit_limit] nvarchar(max)"))
            await command.ExecuteNonQueryAsync();

        var plan = await db.Sync.PlanAsync(db.Registry.Get("Customer"));

        Assert.True(plan.IsEmpty);
        Assert.Contains("credit_limit", Assert.Single(plan.Warnings));
    }

    [SkippableFact]
    public async Task Crud_roundtrips_typed_values()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Territory", Values(("territory_name", "Riyadh")), TestUsers.Manager);

        var created = await db.Documents.InsertAsync("Customer", Values(
            ("customer_name", "Acme"), ("territory", "Riyadh"), ("credit_limit", "1234.5"),
            ("since", "2026-03-09"), ("active", false)), TestUsers.Manager);

        Assert.Equal("CUST-00001", created["name"]);
        Assert.Equal(1234.5m, created["credit_limit"]);
        Assert.Equal(new DateOnly(2026, 3, 9), created["since"]);
        Assert.Equal(false, created["active"]);
        Assert.Equal(DateTimeKind.Utc, ((DateTime)created["creation"]!).Kind);

        var fetched = await db.Documents.GetAsync("Customer", "CUST-00001", TestUsers.Manager);
        Assert.Equal(created["modified"], fetched["modified"]);

        await db.Documents.DeleteAsync("Customer", "CUST-00001", TestUsers.Manager);
        await Assert.ThrowsAsync<NotFoundException>(() => db.Documents.GetAsync("Customer", "CUST-00001", TestUsers.Manager));
    }

    [SkippableFact]
    public async Task Series_names_are_unique_under_concurrency()
    {
        await using var db = await StackAsync();

        var docs = await Task.WhenAll(Enumerable.Range(0, 25).Select(i =>
            db.Documents.InsertAsync("Customer", Values(("customer_name", $"Customer {i}")), TestUsers.Manager)));

        Assert.Equal(25, docs.Select(d => (string)d["name"]!).Distinct().Count());
        Assert.Equal(25, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabCustomer\""));
    }

    [SkippableFact]
    public async Task Unique_violation_becomes_a_conflict()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Manager);

        await Assert.ThrowsAsync<ConflictException>(() =>
            db.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Manager));
    }

    [SkippableFact]
    public async Task Hostile_values_are_stored_literally()
    {
        await using var db = await StackAsync();
        const string hostile = "x'); DROP TABLE \"tabCustomer\"; --";

        await db.Documents.InsertAsync("Customer", Values(("customer_name", hostile)), TestUsers.Manager);
        var found = await db.Documents.ListAsync("Customer", new ListRequest
        {
            Filters = [new RawFilter("customer_name", "=", hostile)],
        }, TestUsers.Manager);

        Assert.Equal(hostile, Assert.Single(found.Data)["customer_name"]);
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabCustomer\""));
    }

    [SkippableFact]
    public async Task List_filters_order_and_pages_in_sql()
    {
        await using var db = await StackAsync();
        foreach (var (name, limit) in new[] { ("Alpha Co", 10), ("Bravo Co", 20), ("Charlie Ltd", 30), ("Delta Co", 40) })
            await db.Documents.InsertAsync("Customer", Values(("customer_name", name), ("credit_limit", limit)), TestUsers.Manager);

        var page = await db.Documents.ListAsync("Customer", new ListRequest
        {
            Filters = [new RawFilter("customer_name", "like", "%Co"), new RawFilter("credit_limit", ">", 10)],
            OrderBy = "credit_limit",
            Descending = true,
            Limit = 1,
            Offset = 1,
        }, TestUsers.Manager);

        Assert.Equal("Bravo Co", Assert.Single(page.Data)["customer_name"]);
        Assert.Equal(2, page.Total);

        var inList = await db.Documents.ListAsync("Customer", new ListRequest
        {
            Filters = [new RawFilter("customer_name", "in", new[] { "Alpha Co", "Charlie Ltd" })],
            OrderBy = "customer_name",
            Descending = false,
        }, TestUsers.Manager);
        Assert.Equal(["Alpha Co", "Charlie Ltd"], inList.Data.Select(r => (string)r["customer_name"]!));
    }

    [SkippableFact]
    public async Task Optimistic_concurrency_works_with_real_timestamps()
    {
        await using var db = await StackAsync();
        var doc = await db.Documents.InsertAsync("Customer", Values(("customer_name", "Acme")), TestUsers.Manager);
        var name = (string)doc["name"]!;
        db.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await db.Documents.UpdateAsync(
            "Customer", name, Values(("credit_limit", 5), ("modified", doc["modified"])), TestUsers.Manager);
        Assert.Equal(5m, updated["credit_limit"]);

        await Assert.ThrowsAsync<ConflictException>(() =>
            db.Documents.UpdateAsync("Customer", name, Values(("credit_limit", 6), ("modified", doc["modified"])), TestUsers.Manager));
    }

    [SkippableFact]
    public async Task If_owner_rule_is_enforced_in_sql()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "A Corp")), TestUsers.SalesA);
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "B Corp")), TestUsers.SalesB);

        var list = await db.Documents.ListAsync("Customer", new ListRequest(), TestUsers.SalesA);

        Assert.Equal("A Corp", Assert.Single(list.Data)["customer_name"]);
        Assert.Equal(1, list.Total);
    }
}
