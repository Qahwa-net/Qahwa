using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresEmailTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Queued_email_is_sent_exactly_once_even_when_schedulers_overlap_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var id = (string)(await db.Documents.InsertAsync("Memo", Values(("title", "Hello")), clerk))["name"]!;

        await db.Email.SendFromDocumentAsync("Memo", id, new EmailRequest(Enumerable.Range(0, 6).Select(i => $"u{i}@example.com").ToList(), "Hi", "Text"), clerk);
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => db.Email.SendPendingAsync()));

        Assert.Equal(6, runs.Sum(r => r.Sent));
        Assert.Equal(6, db.EmailSender.Sent.Select(m => m.To).Distinct().Count());
        Assert.Equal(6, db.EmailSender.Sent.Count);
        Assert.Equal(new EmailRun(0, 0), await db.Email.SendPendingAsync());
    }
}
