using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresServerScriptTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Scripts_validate_change_and_follow_up_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Crate", "fields": [ { "name": "label", "type": "Data" }, { "name": "weight", "type": "Float" }, { "name": "grade", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Check", "fields": [ { "name": "what", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);

        await db.Documents.InsertAsync("Server Script", Values(("title", "Grade"), ("active", 1), ("reference_doctype", "Crate"), ("event", "Validate"), ("script", "if (doc.weight < 0) qahwa.error('No negative weights.'); doc.grade = doc.weight > 100 ? 'Heavy' : 'Light';")), TestUsers.SystemManager);
        await db.Documents.InsertAsync("Server Script", Values(("title", "Follow"), ("active", 1), ("reference_doctype", "Crate"), ("event", "After Save"), ("script", "qahwa.insert('Check', { what: doc.name + ' is ' + doc.grade });")), TestUsers.SystemManager);

        await Assert.ThrowsAsync<DocumentValidationException>(() => db.Documents.InsertAsync("Crate", Values(("label", "bad"), ("weight", -1)), clerk));
        var heavy = await db.Documents.InsertAsync("Crate", Values(("label", "H"), ("weight", 500)), clerk);

        Assert.Equal("Heavy", (await db.Documents.GetAsync("Crate", (string)heavy["name"]!, clerk))["grade"]);
        Assert.Equal($"{heavy["name"]} is Heavy", Assert.Single((await db.Documents.ListAsync("Check", new ListRequest { Fields = ["what"], Limit = 10 }, clerk)).Data)["what"]);
        var scripts = (await db.Documents.ListAsync("Server Script", new ListRequest { Fields = ["title", "last_error"], Limit = 10 }, TestUsers.SystemManager)).Data;
        Assert.All(scripts, s => Assert.Null(s["last_error"]));
    }
}
