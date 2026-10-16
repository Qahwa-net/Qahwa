using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresDeletedDocumentTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_deleted_document_and_its_rows_come_back_from_the_database()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Slip Line", "isChildTable": true, "fields": [ { "name": "what", "type": "Data" }, { "name": "qty", "type": "Int" } ] }"""));
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Slip", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data" }, { "name": "total", "type": "Decimal" }, { "name": "due", "type": "Date" }, { "name": "sent", "type": "Datetime" }, { "name": "done", "type": "Check" }, { "name": "lines", "type": "Table", "options": "Slip Line" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        await db.Documents.InsertAsync("Slip", Values(("title", "s1"), ("total", 99.25m), ("due", "2026-05-06"), ("sent", "2026-05-06T07:08:09Z"), ("done", true),
            ("lines", new object[] { Values(("what", "a"), ("qty", 1)), Values(("what", "b'; DROP TABLE x;--"), ("qty", 2)) })), clerk);

        await db.Documents.DeleteAsync("Slip", "s1", clerk);
        var kept = Assert.Single((await db.Documents.ListAsync("Deleted Document", new ListRequest { Fields = ["name", "deleted_name"], Limit = 10 }, TestUsers.SystemManager)).Data);
        Assert.Equal("s1", kept["deleted_name"]);

        Assert.Equal("s1", await db.DeletedDocuments.RestoreAsync((string)kept["name"]!, TestUsers.Admin));
        var back = await db.Documents.GetAsync("Slip", "s1", clerk);
        Assert.Equal(99.25m, back["total"]);
        Assert.Equal(new DateOnly(2026, 5, 6), back["due"]);
        Assert.Equal(true, back["done"]);
        Assert.Equal(["a", "b'; DROP TABLE x;--"], ((IEnumerable<Dictionary<string, object?>>)back["lines"]!).Select(l => (string)l["what"]!));
        Assert.Empty((await db.Documents.ListAsync("Deleted Document", new ListRequest { Limit = 10 }, TestUsers.SystemManager)).Data);
    }
}
