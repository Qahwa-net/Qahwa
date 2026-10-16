using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresDocumentActionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Actions_set_fields_and_create_documents_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Parcel", "fields": [ { "name": "label", "type": "Data" }, { "name": "state", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Scan", "fields": [ { "name": "what", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);

        await db.Documents.InsertAsync("Document Action", Values(("title", "Mark"), ("active", 1), ("reference_doctype", "Parcel"), ("event", "New"), ("action", "Set Field"), ("target_field", "state"), ("value", "Received {{label}}")), TestUsers.SystemManager);
        await db.Documents.InsertAsync("Document Action", Values(("title", "Scan"), ("active", 1), ("reference_doctype", "Parcel"), ("event", "ValueChange"), ("watch_field", "state"), ("action", "Create Document"), ("target_doctype", "Scan"), ("mapping", "what = {{name}} is {{state}}")), TestUsers.SystemManager);

        var parcel = await db.Documents.InsertAsync("Parcel", Values(("label", "A1")), clerk);
        var saved = await db.Documents.GetAsync("Parcel", (string)parcel["name"]!, clerk);

        Assert.Equal("Received A1", saved["state"]);
        var scans = (await db.Documents.ListAsync("Scan", new ListRequest { Fields = ["what"], Limit = 10 }, clerk)).Data;
        Assert.Empty(scans); // the set-field action does not start the other action: no loops

        await db.Documents.UpdateAsync("Parcel", (string)parcel["name"]!, Values(("state", "Lost")), clerk);
        Assert.Equal($"{parcel["name"]} is Lost", Assert.Single((await db.Documents.ListAsync("Scan", new ListRequest { Fields = ["what"], Limit = 10 }, clerk)).Data)["what"]);
    }
}
