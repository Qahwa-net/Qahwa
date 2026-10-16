using Qahwa.Abstractions;
using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Data.Tests;

public class PostgresBundleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_bundle_made_on_postgres_creates_real_tables_and_columns_when_imported()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Parcel", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true } ] }"""));
        await db.MigrateAsync();

        await db.Designer.CreateAsync(DocTypeLoader.Parse("""{ "name": "Courier", "fields": [ { "name": "full_name", "type": "Data" } ], "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true } ] }"""), TestUsers.SystemManager);
        await db.Customization.SaveAsync("Parcel", new Customization { CustomFields = [new DocFieldMeta { Name = "custom_weight", Type = FieldType.Float, Label = "Weight" }] }, null, TestUsers.SystemManager);
        var file = BundleService.ToJson(await db.Bundle.ExportAsync("logistics", null, TestUsers.SystemManager));

        await using var other = await postgres.CreateStackAsync();
        other.Registry.Add(DocTypeLoader.Parse("""{ "name": "Parcel", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true } ] }"""));
        await other.MigrateAsync();
        var report = await other.Bundle.ImportAsync(BundleService.FromJson(file), dryRun: false, TestUsers.SystemManager);

        Assert.Equal(0, report.Failed);
        Assert.Equal(2, report.Applied);
        var courier = await other.Documents.InsertAsync("Courier", new Dictionary<string, object?> { ["full_name"] = "Sam" }, TestUsers.SystemManager);
        Assert.Equal("Sam", courier["full_name"]);
        var parcel = await other.Documents.InsertAsync("Parcel", new Dictionary<string, object?> { ["label"] = "A", ["custom_weight"] = 2.5 }, TestUsers.SystemManager);
        Assert.Equal(2.5, Convert.ToDouble(parcel["custom_weight"]));
    }
}
