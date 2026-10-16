using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Data.Tests;

public class PostgresBackupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Basket = """{ "name": "Basket", "autoname": "BSK-.####", "fields": [ { "name": "customer", "type": "Data" }, { "name": "total", "type": "Currency" }, { "name": "due", "type": "Date" }, { "name": "paid", "type": "Check" }, { "name": "stamp", "type": "Datetime" }, { "name": "lines", "type": "Table", "options": "Basket Line" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }""";
    private const string Line = """{ "name": "Basket Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" } ] }""";

    [SkippableFact]
    public async Task A_backup_made_on_one_postgres_database_restores_into_another_with_every_type_and_the_counters()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var source = await postgres.CreateStackAsync();
        await using var target = await postgres.CreateStackAsync();
        foreach (var db in new[] { source, target })
        {
            db.Registry.Add(DocTypeLoader.Parse(Line));
            db.Registry.Add(DocTypeLoader.Parse(Basket));
            await db.MigrateAsync();
        }

        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var made = await source.Documents.InsertAsync("Basket", new Dictionary<string, object?>
        {
            ["customer"] = "Acme", ["total"] = 1234.5m, ["due"] = "2026-03-04", ["paid"] = true, ["stamp"] = "2026-03-04T10:30:00Z",
            ["lines"] = new List<Dictionary<string, object?>> { new() { ["item"] = "Beans", ["qty"] = 2 }, new() { ["item"] = "Milk", ["qty"] = 5 } },
        }, clerk);
        await source.Documents.InsertAsync("Basket", new Dictionary<string, object?> { ["customer"] = "Second" }, clerk);

        var stream = new MemoryStream();
        await source.Backup.WriteAsync(stream, TestUsers.Admin);
        stream.Position = 0;
        var report = await target.Backup.RestoreAsync(stream, dryRun: false, TestUsers.Admin);

        Assert.Equal(2, report.Tables.Single(t => t.DocType == "Basket").Inserted);
        Assert.Equal(2, report.Tables.Single(t => t.DocType == "Basket Line").Inserted);
        var name = (string)made["name"]!;
        var a = await source.Documents.GetAsync("Basket", name, clerk);
        var b = await target.Documents.GetAsync("Basket", name, clerk);
        foreach (var field in new[] { "customer", "total", "due", "paid", "stamp", "creation", "modified", "owner" })
            Assert.Equal(a[field]?.ToString(), b[field]?.ToString());
        Assert.Equal(2, ((IEnumerable<object?>)b["lines"]!).Count());

        var next = await target.Documents.InsertAsync("Basket", new Dictionary<string, object?> { ["customer"] = "Third" }, clerk);
        Assert.Equal("BSK-0003", next["name"]);
        Assert.Equal(0, (await target.Backup.RestoreAsync(await Again(source), false, TestUsers.Admin)).Tables.Single(t => t.DocType == "Basket").Inserted); // nothing new the second time
    }

    private static async Task<MemoryStream> Again(PgStack db)
    {
        var stream = new MemoryStream();
        await db.Backup.WriteAsync(stream, TestUsers.Admin);
        stream.Position = 0;
        return stream;
    }
}
