using Qahwa.Core;

namespace Qahwa.Data.Tests;

public class PostgresVirtualDocTypeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_virtual_doctype_gets_no_table_while_a_real_one_does()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Rate Feed", "isVirtual": true, "fields": [ { "name": "label", "type": "Data" } ] }"""));
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Real Thing", "fields": [ { "name": "label", "type": "Data" } ] }"""));

        await db.MigrateAsync();

        Assert.False(await db.TableExistsAsync("tabRate Feed"));
        Assert.True(await db.TableExistsAsync("tabReal Thing"));
    }
}
