using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresWorkspaceTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Workspaces_are_stored_and_filtered_for_each_person_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket Form", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "create": true } ] }
            """));
        await db.MigrateAsync();
        await db.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var other = new UserContext("o@example.com", ["Other"]);

        var made = await db.Workspaces.CreateAsync(new WorkspaceRequest("Support", 1, ["Clerk"],
        [
            new WorkspaceBlock { Type = WorkspaceBlock.Heading, Text = "Tickets" },
            new WorkspaceBlock { Type = WorkspaceBlock.Shortcuts, Items = [new WorkspaceShortcut("Open", "DocType", "Ticket Form")] },
        ]), TestUsers.SystemManager);

        var view = Assert.Single(await db.Workspaces.MineAsync(clerk));
        Assert.Equal(["/app/Ticket%20Form"], view.Blocks[1].Shortcuts!.Select(s => s.Path));
        Assert.Empty(await db.Workspaces.MineAsync(other));

        await db.Workspaces.DeleteAsync(made.Info.Id, TestUsers.SystemManager);
        Assert.Empty(await db.Workspaces.MineAsync(clerk));
    }
}
