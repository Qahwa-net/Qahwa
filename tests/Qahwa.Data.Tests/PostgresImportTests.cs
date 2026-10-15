using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresImportTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_csv_file_imports_and_updates_documents_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Person Row", "autoname": "PR-.####", "fields": [
                { "name": "full_name", "type": "Data", "required": true }, { "name": "age", "type": "Int" }, { "name": "joined", "type": "Date" }, { "name": "vip", "type": "Check" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);

        var result = await db.DataImport.RunAsync("Person Row", "full_name,age,joined,vip\r\nAnn,41,2026-03-04,yes\r\nBo,x,2026-01-01,no\r\n\"Cy, Jr\",7,,1\r\n", "Insert", false, clerk);

        Assert.Equal((2, 1), (result.Created, result.Failed));
        var rows = (await db.Documents.ListAsync("Person Row", new ListRequest { OrderBy = "creation", Descending = false, Limit = 10 }, clerk)).Data;
        Assert.Equal(["Ann", "Cy, Jr"], rows.Select(r => (string)r["full_name"]!));
        Assert.Equal(new DateOnly(2026, 3, 4), rows[0]["joined"]);

        var update = await db.DataImport.RunAsync("Person Row", $"name,age\r\n{rows[0]["name"]},42\r\n", "Update", false, clerk);
        Assert.Equal(1, update.Updated);
        Assert.Equal(42L, Convert.ToInt64((await db.Documents.GetAsync("Person Row", (string)rows[0]["name"]!, clerk))["age"]));
    }
}
