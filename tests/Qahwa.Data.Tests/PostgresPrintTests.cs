using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresPrintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Formats_and_letter_heads_are_stored_and_documents_print_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Line", "isChildTable": true, "fields": [ { "name": "what", "type": "Data" } ] }
            """));
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quote Doc", "fields": [ { "name": "client", "type": "Data", "label": "Client" }, { "name": "lines", "type": "Table", "options": "Line" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var values = Values(("client", "Acme & Sons"));
        values["lines"] = new List<Dictionary<string, object?>> { Values(("what", "Tea")), Values(("what", "<i>Sugar</i>")) };
        var id = (string)(await db.Documents.InsertAsync("Quote Doc", values, clerk))["name"]!;

        await db.Print.SaveLetterHeadAsync(new LetterHeadRequest("Main", "<h2>HEAD</h2>", "<small>FOOT</small>", IsDefault: true), TestUsers.SystemManager);
        await db.Print.SaveFormatAsync(new PrintFormatRequest("Plain", "Quote Doc", "{{label:client}}={{client}}{{#each lines}}[{{@index}}:{{what}}]{{/each}}", IsDefault: true), TestUsers.SystemManager);

        var html = (await db.Print.RenderAsync("Quote Doc", id, null, null, clerk)).Html;

        Assert.Contains("Client=Acme &amp; Sons[1:Tea][2:&lt;i&gt;Sugar&lt;/i&gt;]", html);
        Assert.Contains("<h2>HEAD</h2>", html);
        Assert.Contains("<small>FOOT</small>", html);
        Assert.Equal(["Plain"], (await db.Print.ChoicesAsync("Quote Doc", clerk)).Formats.Select(f => f.Name));
        await db.Print.DeleteFormatAsync("Plain", TestUsers.SystemManager);
        Assert.Contains("<th>Client</th><td>Acme &amp; Sons</td>", (await db.Print.RenderAsync("Quote Doc", id, null, "none", clerk)).Html);
    }
}
