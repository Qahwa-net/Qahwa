using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresAutoRepeatTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_repeating_document_copies_itself_on_postgres_exactly_once_per_occurrence()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Line Item", "isChildTable": true, "fields": [ { "name": "what", "type": "Data" } ] }
            """));
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Retainer", "autoname": "RET-.####", "fields": [ { "name": "client", "type": "Data" }, { "name": "billed_on", "type": "Date" }, { "name": "lines", "type": "Table", "options": "Line Item" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        await db.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await db.Documents.InsertAsync("User", Values(("email", clerk.User), ("full_name", "Clerk"), ("enabled", true)), TestUsers.Admin);
        await db.Documents.InsertAsync("Has Role", Values(("user", clerk.User), ("role", "Clerk")), TestUsers.Admin);

        var values = Values(("client", "Acme"), ("billed_on", "2026-01-01"));
        values["lines"] = new List<Dictionary<string, object?>> { Values(("what", "Hosting")) };
        var id = (string)(await db.Documents.InsertAsync("Retainer", values, clerk))["name"]!;
        var tomorrow = DateOnly.FromDateTime(db.Clock.GetUtcNow().UtcDateTime).AddDays(1).ToString("yyyy-MM-dd");
        var repeat = await db.AutoRepeat.CreateAsync("Retainer", id, new AutoRepeatRequest("Daily", tomorrow, null, ["billed_on"]), clerk);

        db.Clock.Advance(TimeSpan.FromDays(1));
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => db.AutoRepeat.RunDueAsync()));

        Assert.Equal(1, runs.Sum(r => r.Created));
        var all = await db.Documents.ListAsync("Retainer", new ListRequest { Fields = ["name"], OrderBy = "name", Descending = false, Limit = 10 }, clerk);
        Assert.Equal(2, all.Data.Count);
        var copy = await db.Documents.GetAsync("Retainer", (string)all.Data.Single(r => (string)r["name"]! != id)["name"]!, clerk);
        Assert.Equal(DateOnly.Parse(tomorrow), copy["billed_on"]);
        Assert.Single((IEnumerable<object?>)copy["lines"]!);
        Assert.Equal(1L, Assert.Single(await db.AutoRepeat.ForDocumentAsync("Retainer", id, clerk)).Created);

        await db.AutoRepeat.DeleteAsync(repeat.Id, clerk);
        Assert.Empty(await db.AutoRepeat.ListAsync(clerk));
    }
}
