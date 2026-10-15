using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresWebhookTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Hooks_are_stored_reloaded_and_deliveries_are_claimed_once_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Parcel", "fields": [ { "name": "label", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var set = new WebhookSet { DocType = "Parcel", Hooks = [new Webhook { Name = "Tell", Url = "https://hooks.example.com/p", Secret = "a-long-enough-shared-secret" }] };
        var saved = await db.WebhookManager.SaveAsync("Parcel", set, null, TestUsers.SystemManager);
        Assert.DoesNotContain("a-long-enough-shared-secret", System.Text.Json.JsonSerializer.Serialize(saved));

        for (var i = 0; i < 4; i++)
            await db.Documents.InsertAsync("Parcel", Values(("label", $"P{i}")), clerk);
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => db.Webhooks.DeliverPendingAsync()));

        Assert.Equal(4, runs.Sum(r => r.Sent));
        Assert.Equal(4, db.WebhookSender.Sent.Select(s => s.Headers["X-Qahwa-Delivery"]).Distinct().Count());

        var fresh = new WebhookCatalog();
        Assert.Empty(await new WebhookManager(db.Registry, db.WebhookStore, fresh, new PermissionService(db.Overrides), db.Clock).LoadAsync());
        Assert.NotNull(fresh.Find("Parcel", "Tell"));
        await Assert.ThrowsAsync<ConflictException>(() => db.WebhookManager.SaveAsync("Parcel", set, null, TestUsers.SystemManager));
    }
}
