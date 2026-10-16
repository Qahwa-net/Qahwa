using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresFieldTypeTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly string Png = "data:image/png;base64," + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9]);

    [SkippableFact]
    public async Task The_new_field_types_get_real_columns_and_keep_their_values_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Visit", "fields": [ { "name": "stars", "type": "Rating" }, { "name": "spent", "type": "Duration" }, { "name": "tint", "type": "Color" }, { "name": "code", "type": "Barcode" }, { "name": "place", "type": "Geolocation" }, { "name": "sign", "type": "Signature" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);

        var made = await db.Documents.InsertAsync("Visit", Values(("stars", 5), ("spent", 3600), ("tint", "#FF0000"), ("code", "ABC-123"), ("place", """{"lat":24.7,"lng":46.7}"""), ("sign", Png)), clerk);
        var saved = await db.Documents.GetAsync("Visit", (string)made["name"]!, clerk);

        Assert.Equal(5, Convert.ToInt32(saved["stars"]));
        Assert.Equal(3600, Convert.ToInt32(saved["spent"]));
        Assert.Equal("#ff0000", saved["tint"]);
        Assert.Equal("ABC-123", saved["code"]);
        Assert.Equal(Png, saved["sign"]);
        Assert.Equal(db.IsPostgres ? "integer" : "int", await db.ColumnTypeAsync("tabVisit", "stars"));
        Assert.Equal(db.IsPostgres ? "integer" : "int", await db.ColumnTypeAsync("tabVisit", "spent"));
        Assert.Equal(db.IsPostgres ? "character varying" : "nvarchar", await db.ColumnTypeAsync("tabVisit", "tint"));
        Assert.Equal(db.IsPostgres ? "text" : "nvarchar", await db.ColumnTypeAsync("tabVisit", "sign"));
        var top = await db.Documents.ListAsync("Visit", new ListRequest { Filters = [new RawFilter("stars", ">=", 4)], Fields = ["name"], Limit = 5 }, clerk);
        Assert.Single(top.Data);
    }
}
