using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresNamingRuleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Naming_rules_and_expressions_name_documents_in_the_database()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Contract", "autoname": "CT-.###", "fields": [ { "name": "party", "type": "Data" }, { "name": "value", "type": "Int" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        await db.Documents.InsertAsync("Document Naming Rule", Values(("document_type", "Contract"), ("pattern", "BIG-{YYYY}-.###"), ("condition", "value >= 1000"), ("priority", 1)), TestUsers.Admin);
        await db.Documents.InsertAsync("Document Naming Rule", Values(("document_type", "Contract"), ("pattern", "expression:upper(party) + \"-\" + text(value)"), ("condition", "value == 7"), ("priority", 2)), TestUsers.Admin);

        var big = await db.Documents.InsertAsync("Contract", Values(("party", "acme"), ("value", 5000)), clerk);
        var seven = await db.Documents.InsertAsync("Contract", Values(("party", "o'brien"), ("value", 7)), clerk);
        var small = await db.Documents.InsertAsync("Contract", Values(("party", "x"), ("value", 1)), clerk);
        var big2 = await db.Documents.InsertAsync("Contract", Values(("party", "y"), ("value", 2000)), clerk);

        Assert.Matches(@"^BIG-\d{4}-001$", (string)big["name"]!);
        Assert.Equal("O'BRIEN-7", seven["name"]);
        Assert.Equal("CT-001", small["name"]);
        Assert.Matches(@"^BIG-\d{4}-002$", (string)big2["name"]!);
    }
}
