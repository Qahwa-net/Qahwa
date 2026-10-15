using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

public class PostgresPermissionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        return await postgres.CreateStackAsync();
    }

    private static DocPermMeta Rule(string role, bool read = false, bool write = false, int level = 0) =>
        new() { Role = role, Read = read, Write = write, PermLevel = level };

    [SkippableFact]
    public async Task Overrides_round_trip_survive_reload_and_reset()
    {
        await using var db = await StackAsync();

        var saved = await db.PermissionManager.SaveAsync("Territory", [Rule("Sales User", read: true)], null, TestUsers.SystemManager);
        var stored = await db.PermissionStore.GetAsync("Territory");
        Assert.NotNull(stored);

        var overrides = new PermissionOverrides();
        var failures = await new PermissionManager(db.Registry, db.PermissionStore, overrides, db.Clock).LoadAsync();
        Assert.Empty(failures);
        Assert.Equal("Sales User", Assert.Single(overrides.Get("Territory")!).Role);

        await Assert.ThrowsAsync<ConflictException>(() =>
            db.PermissionManager.SaveAsync("Territory", [Rule("X", read: true)], null, TestUsers.SystemManager));
        await db.PermissionManager.SaveAsync("Territory", [Rule("Sales User", read: true, write: true)], saved.Modified, TestUsers.SystemManager);

        await db.PermissionManager.ResetAsync("Territory", TestUsers.SystemManager);
        Assert.Null(await db.PermissionStore.GetAsync("Territory"));
    }

    [SkippableFact]
    public async Task Hidden_fields_are_left_out_of_sql_lists_and_filters_on_them_are_refused()
    {
        await using var db = await StackAsync();
        var meta = DocTypeLoader.Parse("""
            { "name": "Payroll", "fields": [ { "name": "who", "type": "Data" }, { "name": "pay", "type": "Currency", "permLevel": 1 } ],
              "permissions": [ { "role": "Sales User", "read": true, "write": true, "create": true } ] }
            """);
        db.Registry.Add(meta);
        await db.Sync.ApplyAsync(await db.Sync.PlanAsync(meta));
        await db.Documents.InsertAsync("Payroll", Values(("who", "A"), ("pay", 10)), TestUsers.Admin);

        var list = await db.Documents.ListAsync("Payroll", new ListRequest(), TestUsers.SalesA);
        Assert.All(list.Data, row => Assert.False(row.ContainsKey("pay")));

        var request = new ListRequest { Fields = ["pay"] };
        await Assert.ThrowsAnyAsync<Exception>(() => db.Documents.ListAsync("Payroll", request, TestUsers.SalesA));
    }
}

public class PostgresUserPermissionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Restricted_users_only_get_rows_linked_to_their_values_or_with_no_link()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        var admin = TestUsers.Admin;
        var rep = new UserContext("rep@example.com", ["Sales Manager"]);

        foreach (var territory in new[] { "North", "South" })
            await db.Documents.InsertAsync("Territory", Values(("territory_name", territory)), admin);
        foreach (var (name, territory) in new (string, string?)[] { ("In North", "North"), ("In South", "South"), ("Nowhere", null) })
            await db.Documents.InsertAsync("Customer", Values(("customer_name", name), ("territory", territory)), admin);
        await db.Documents.InsertAsync("User", Values(("email", rep.User), ("full_name", "Rep")), admin);
        await db.Documents.InsertAsync("User Permission", Values(("user", rep.User), ("allow", "Territory"), ("for_value", "North"), ("apply_to_all_doctypes", true)), admin);

        var seen = (await db.Documents.ListAsync("Customer", new ListRequest { Limit = 100 }, rep)).Data.Select(r => (string)r["customer_name"]!).Order().ToList();

        Assert.Equal(["In North", "Nowhere"], seen);
        Assert.Equal(1, (await db.Documents.ListAsync("Territory", new ListRequest(), rep)).Total);
    }
}
