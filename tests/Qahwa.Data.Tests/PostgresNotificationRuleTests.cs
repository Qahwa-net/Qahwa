using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresNotificationRuleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Rules_are_stored_reloaded_and_notify_on_postgres_including_the_daily_scan()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Contract", "fields": [ { "name": "party", "type": "Data" }, { "name": "ends_on", "type": "Date" }, { "name": "status", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var watcher = new UserContext("w@example.com", ["Clerk"]);
        await db.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        foreach (var person in new[] { clerk, watcher })
        {
            await db.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
            await db.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", "Clerk")), TestUsers.Admin);
        }

        var set = new NotificationRuleSet
        {
            DocType = "Contract",
            Rules =
            [
                new NotificationRule { Name = "New", Event = NotificationRule.New, Users = [watcher.User], Message = "New {{party}}" },
                new NotificationRule { Name = "Ending", Event = NotificationRule.DaysBefore, DateField = "ends_on", Days = 7, Roles = ["Clerk"], Message = "{{party}} ends {{ends_on}}" },
            ],
        };
        var saved = await db.NotificationRuleManager.SaveAsync("Contract", set, null, TestUsers.SystemManager);

        var today = DateOnly.FromDateTime(db.Clock.GetUtcNow().UtcDateTime);
        var id = (string)(await db.Documents.InsertAsync("Contract", Values(("party", "Acme"), ("ends_on", today.AddDays(7))), clerk))["name"]!;
        var alerts = (await db.Notifications.ListAsync(watcher, unreadOnly: false, limit: 20, before: null)).Where(n => n.Type == "Alert").ToList();
        Assert.Equal(["New Acme"], alerts.Select(n => n.Subject));

        Assert.Equal(2, await db.NotificationRules.RunScheduledAsync()); // everyone with the role (the system caused it), once each
        Assert.Equal(0, await db.NotificationRules.RunScheduledAsync());
        Assert.Contains(await db.Notifications.ListAsync(watcher, unreadOnly: false, limit: 20, before: null), n => n.Subject == $"Acme ends {today.AddDays(7):yyyy-MM-dd}");
        Assert.Equal(id, alerts[0].DocName);

        var fresh = new NotificationRuleCatalog();
        Assert.Empty(await new NotificationRuleManager(db.Registry, db.NotificationRuleStore, fresh, db.Repository, new PermissionService(db.Overrides), db.Clock).LoadAsync());
        Assert.Equal(2, fresh.For("Contract")!.Rules.Count);
        await Assert.ThrowsAsync<ConflictException>(() => db.NotificationRuleManager.SaveAsync("Contract", set, null, TestUsers.SystemManager));
        Assert.NotNull(saved.Modified);
    }
}
