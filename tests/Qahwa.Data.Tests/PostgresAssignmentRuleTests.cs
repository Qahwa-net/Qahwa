using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresAssignmentRuleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Rules_are_stored_reloaded_and_assign_documents_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Lead", "fields": [ { "name": "region", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        foreach (var email in new[] { "c@example.com", "x@example.com", "y@example.com" })
            await db.Documents.InsertAsync("User", Values(("email", email), ("full_name", email), ("enabled", true)), TestUsers.Admin);

        var set = new AssignmentRuleSet
        {
            DocType = "Lead",
            Rules = [new AssignmentRule { Name = "North", Users = ["x@example.com", "y@example.com"], Condition = "region == 'North'", CloseCondition = "region == 'Closed'" }],
        };
        var saved = await db.AssignmentRuleManager.SaveAsync("Lead", set, null, TestUsers.SystemManager);

        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            db.Clock.Advance(TimeSpan.FromMinutes(1));
            names.Add((string)(await db.Documents.InsertAsync("Lead", Values(("region", "North")), clerk))["name"]!);
        }

        var people = new List<string>();
        foreach (var name in names)
            people.Add(Assert.Single(await db.Assignments.ForDocumentAsync("Lead", name, clerk)).User);
        Assert.Equal(["x@example.com", "y@example.com", "x@example.com"], people);

        await db.Documents.UpdateAsync("Lead", names[0], Values(("region", "Closed")), clerk);
        Assert.Empty(await db.Assignments.ForDocumentAsync("Lead", names[0], clerk)); // withdrawn assignments are not listed

        var fresh = new AssignmentRuleCatalog();
        Assert.Empty(await new AssignmentRuleManager(db.Registry, db.AssignmentRuleStore, fresh, db.Repository, new PermissionService(db.Overrides), db.Clock).LoadAsync());
        Assert.NotNull(fresh.For("Lead"));
        await Assert.ThrowsAsync<ConflictException>(() => db.AssignmentRuleManager.SaveAsync("Lead", set, null, TestUsers.SystemManager));
        Assert.NotNull(saved.Modified);
    }
}
