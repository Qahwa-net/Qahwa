using System.Text;
using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresFileTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Files_are_recorded_listed_and_removed_with_their_document_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Dossier", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var id = (string)(await db.Documents.InsertAsync("Dossier", Values(("title", "File me")), clerk))["name"]!;

        var file = await db.Files.UploadAsync("Dossier", id, "notes.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), clerk);

        Assert.Equal("hello", await new StreamReader((await db.Files.OpenAsync(file.Id, clerk)).Content).ReadToEndAsync());
        Assert.Single(await db.Files.ListAsync("Dossier", id, clerk));
        Assert.Equal(1, db.FileStorage.Count);

        await db.Documents.DeleteAsync("Dossier", id, clerk);

        Assert.Equal(0, db.FileStorage.Count);
    }
}
