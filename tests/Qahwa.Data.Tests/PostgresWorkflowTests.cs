using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresWorkflowTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_workflow_is_stored_reloaded_and_drives_documents_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Expense", "isSubmittable": true, "fields": [ { "name": "amount", "type": "Currency" }, { "name": "workflow_state", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Boss", "read": true } ] }
            """));
        await db.MigrateAsync();
        var definition = new WorkflowDefinition
        {
            DocType = "Expense",
            StateField = "workflow_state",
            States = [new() { Name = "Draft" }, new() { Name = "Approved", DocStatus = 1 }],
            Transitions = [new() { From = "Draft", Action = "Approve", To = "Approved", Roles = ["Boss"], Condition = "amount < 500" }],
        };

        var saved = await db.Workflows.SaveAsync("Expense", definition, null, TestUsers.SystemManager);
        var clerk = new UserContext("c@example.com", ["Clerk"]);
        var boss = new UserContext("b@example.com", ["Boss"]);
        var small = (string)(await db.Documents.InsertAsync("Expense", Values(("amount", 100)), clerk))["name"]!;
        var large = (string)(await db.Documents.InsertAsync("Expense", Values(("amount", 900)), clerk))["name"]!;

        Assert.Equal(["Approve"], (await db.Documents.WorkflowStatusAsync("Expense", small, boss))!.Actions.Select(a => a.Action));
        Assert.Empty((await db.Documents.WorkflowStatusAsync("Expense", large, boss))!.Actions);
        await db.Documents.ApplyWorkflowActionAsync("Expense", small, "Approve", null, boss);
        var approved = await db.Documents.GetAsync("Expense", small, boss);
        Assert.Equal(("Approved", 1), ((string)approved["workflow_state"]!, Convert.ToInt32(approved["docstatus"])));

        var fresh = new WorkflowCatalog();
        Assert.Empty(await new WorkflowManager(db.Registry, db.WorkflowStore, fresh, new PermissionService(db.Overrides), db.Clock).LoadAsync());
        Assert.NotNull(fresh.For("Expense"));
        await Assert.ThrowsAsync<ConflictException>(() => db.Workflows.SaveAsync("Expense", definition, null, TestUsers.SystemManager));
        Assert.NotNull(saved.Modified);
    }
}
