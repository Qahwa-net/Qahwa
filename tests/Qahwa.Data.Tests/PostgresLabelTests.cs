using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresLabelTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Tags_and_likes_filter_lists_in_the_database()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""{ "name": "Case File", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }"""));
        await db.MigrateAsync();
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var other = new UserContext("o@example.com", ["Clerk"]);
        foreach (var title in new[] { "a", "b", "c" })
            await db.Documents.InsertAsync("Case File", Values(("title", title)), clerk);
        await db.Labels.AddTagAsync("Case File", "a", "Urgent", clerk);
        await db.Labels.AddTagAsync("Case File", "c", "urgent", clerk);
        await db.Labels.AddTagAsync("Case File", "b", "x'; DROP TABLE y;--", clerk);
        await db.Labels.SetLikeAsync("Case File", "c", true, other);

        async Task<string[]> Names(UserContext who, params RawFilter[] filters) =>
            (await db.Documents.ListAsync("Case File", new ListRequest { Fields = ["name"], OrderBy = "name", Descending = false, Filters = filters }, who)).Data.Select(r => (string)r["name"]!).ToArray();

        Assert.Equal(["a", "c"], await Names(clerk, new RawFilter("_tag", "=", "urgent")));
        Assert.Equal(["c"], await Names(other, new RawFilter("_liked_by", "=", "__user")));
        Assert.Equal(["c"], await Names(clerk, new RawFilter("_tag", "=", "urgent"), new RawFilter("_liked_by", "=", "o@example.com")));
        Assert.Equal(2, (await db.Documents.ListAsync("Case File", new ListRequest { Filters = [new RawFilter("_tag", "=", "urgent")] }, clerk)).Total);
        Assert.Equal([new TagCount("urgent", 2), new TagCount("x'; drop table y;--", 1)], await db.Labels.PopularTagsAsync("Case File", clerk));
    }
}
