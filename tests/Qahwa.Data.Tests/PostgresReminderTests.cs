using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresReminderTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_due_reminder_is_sent_exactly_once_even_when_servers_race()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "autoname": "MEMO-.####", "fields": [ { "name": "subject", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        await db.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await db.Documents.InsertAsync("User", Values(("email", clerk.User), ("full_name", "Clerk"), ("enabled", true)), TestUsers.Admin);
        await db.Documents.InsertAsync("Has Role", Values(("user", clerk.User), ("role", "Clerk")), TestUsers.Admin);
        var memo = (string)(await db.Documents.InsertAsync("Memo", Values(("subject", "Renew")), clerk))["name"]!;

        await db.Reminders.CreateAsync("Memo", memo, new ReminderRequest(db.Clock.GetUtcNow().AddHours(1).ToString("O"), "Call them"), clerk);
        await db.Reminders.CreateAsync("Memo", memo, new ReminderRequest(db.Clock.GetUtcNow().AddDays(3).ToString("O")), clerk);

        Assert.Equal((0, 0), await db.Reminders.RunDueAsync());
        db.Clock.Advance(TimeSpan.FromHours(2));
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => db.Reminders.RunDueAsync()));

        Assert.Equal(1, runs.Sum(r => r.Sent));
        var inbox = await db.Notifications.ListAsync(clerk, unreadOnly: false, limit: 10, before: null);
        Assert.Equal("Reminder: Call them", Assert.Single(inbox).Subject);
        var pending = Assert.Single(await db.Reminders.ListPendingAsync(clerk));
        Assert.True(pending.RemindAt > db.Clock.GetUtcNow().UtcDateTime);
    }
}
