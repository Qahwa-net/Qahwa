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

public class PostgresShareTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Shared_documents_appear_in_sql_lists_for_own_only_and_role_less_users()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        foreach (var user in new[] { TestUsers.SalesA, TestUsers.SalesB, TestUsers.Nobody })
            await db.Documents.InsertAsync("User", Values(("email", user.User), ("full_name", user.User)), TestUsers.Admin);
        var mine = (string)(await db.Documents.InsertAsync("Customer", Values(("customer_name", "A1")), TestUsers.SalesA))["name"]!;
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "A2")), TestUsers.SalesA);
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "B1")), TestUsers.SalesB);

        await db.ShareManager.SetAsync("Customer", mine, TestUsers.SalesB.User, true, false, TestUsers.SalesA);
        await db.ShareManager.SetAsync("Customer", mine, TestUsers.Nobody.User, true, false, TestUsers.SalesA);

        var b = (await db.Documents.ListAsync("Customer", new ListRequest { Limit = 100 }, TestUsers.SalesB)).Data.Select(r => (string)r["customer_name"]!).Order().ToList();
        var nobody = (await db.Documents.ListAsync("Customer", new ListRequest(), TestUsers.Nobody)).Data.Select(r => r["customer_name"]).ToList();

        Assert.Equal(["A1", "B1"], b);
        Assert.Equal(["A1"], nobody);
        await db.Documents.DeleteAsync("Customer", mine, TestUsers.Admin);
        Assert.Empty(await db.Shares.RowsForDocumentAsync("Customer", mine));
    }
}

public class PostgresSpecialTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly UserContext Editor = new("editor@example.com", ["Editor"]);

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        var db = await postgres.CreateStackAsync();
        foreach (var json in new[]
        {
            """{ "name": "Team Member", "isChildTable": true, "fields": [ { "name": "person", "type": "Data" } ] }""",
            """
            { "name": "Team", "autoname": "field:team_name", "allowRename": true,
              "fields": [ { "name": "team_name", "type": "Data", "required": true }, { "name": "members", "type": "Table", "options": "Team Member" } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true, "delete": true } ] }
            """,
            """
            { "name": "Work Item", "autoname": "WI-.####",
              "fields": [ { "name": "title", "type": "Data" }, { "name": "team", "type": "Link", "options": "Team" } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true } ] }
            """,
            """
            { "name": "Region", "isTree": true, "autoname": "field:region_name",
              "fields": [ { "name": "region_name", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true, "delete": true } ] }
            """,
            """
            { "name": "App Settings", "isSingle": true, "fields": [ { "name": "limit", "type": "Int" } ],
              "permissions": [ { "role": "Editor", "read": true, "write": true, "create": true } ] }
            """,
        })
        {
            var meta = DocTypeLoader.Parse(json);
            db.Registry.Add(meta);
            await db.Sync.ApplyAsync(await db.Sync.PlanAsync(meta));
        }

        return db;
    }

    [SkippableFact]
    public async Task Rename_and_merge_run_in_one_transaction_against_postgres()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Team", Values(("team_name", "Alpha"), ("members", new[] { Values(("person", "Ann")) })), Editor);
        await db.Documents.InsertAsync("Team", Values(("team_name", "Beta"), ("members", new[] { Values(("person", "Bob")) })), Editor);
        var item = (string)(await db.Documents.InsertAsync("Work Item", Values(("title", "T"), ("team", "Alpha")), Editor))["name"]!;

        await db.Documents.RenameAsync("Team", "Alpha", "Alpha 2", false, Editor);
        Assert.Equal("Alpha 2", (await db.Documents.GetAsync("Work Item", item, Editor))["team"]);
        var members = (IEnumerable<Dictionary<string, object?>>)(await db.Documents.GetAsync("Team", "Alpha 2", Editor))["members"]!;
        Assert.Equal(["Ann"], members.Select(m => m["person"]));

        await Assert.ThrowsAsync<ConflictException>(() => db.Documents.RenameAsync("Team", "Alpha 2", "Beta", false, Editor));
        await db.Documents.RenameAsync("Team", "Alpha 2", "Beta", true, Editor);
        Assert.Equal("Beta", (await db.Documents.GetAsync("Work Item", item, Editor))["team"]);
        Assert.Equal(1, (await db.Documents.ListAsync("Team", new ListRequest(), Editor)).Total);
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabTeam Member\""));
    }

    [SkippableFact]
    public async Task Series_counters_trees_and_singles_work_against_postgres()
    {
        await using var db = await StackAsync();
        var manager = new NamingSeriesManager(db.Registry, db.Repository, new PermissionService(db.Overrides));
        await db.Documents.InsertAsync("Work Item", Values(("title", "one")), Editor);

        Assert.Equal(1, (await manager.ListAsync(TestUsers.SystemManager)).Single(s => s.Prefix == "WI-").Current);
        await manager.SetAsync("WI-", 50, TestUsers.SystemManager);
        Assert.Equal("WI-0051", (await db.Documents.InsertAsync("Work Item", Values(("title", "two")), Editor))["name"]);

        await db.Documents.InsertAsync("Region", Values(("region_name", "World"), ("is_group", true)), Editor);
        await db.Documents.InsertAsync("Region", Values(("region_name", "Europe"), ("parent_node", "World")), Editor);
        Assert.Equal(["Europe"], (await db.Documents.TreeChildrenAsync("Region", "World", Editor)).Select(r => r["name"]));
        await Assert.ThrowsAsync<DocumentValidationException>(() => db.Documents.DeleteAsync("Region", "World", Editor));

        await db.Documents.UpdateAsync("App Settings", "App Settings", Values(("limit", 3)), Editor);
        Assert.Equal(7, (await db.Documents.UpdateAsync("App Settings", "App Settings", Values(("limit", 7)), Editor))["limit"]);
    }
}

public class PostgresLinkSearchTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Link_search_ignores_case_and_shows_titles_against_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "Acme Trading")), TestUsers.Admin);
        await db.Documents.InsertAsync("Customer", Values(("customer_name", "Globex")), TestUsers.Admin);

        var found = await db.Documents.SearchLinkAsync("Customer", "ACME", 10, TestUsers.Admin);

        Assert.Equal("Acme Trading", Assert.Single(found).Label);
        Assert.Equal(["Acme Trading"], (await db.Documents.SearchLinkAsync("Customer", "trad", 10, TestUsers.Admin)).Select(o => o.Label));
    }
}

public class PostgresCustomizationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_custom_field_gets_a_real_column_survives_reload_and_keeps_its_data_after_reset()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        var customization = new Customization
        {
            CustomFields = [new DocFieldMeta { Name = "custom_phone", Type = Qahwa.Abstractions.FieldType.Data, Label = "Phone" }],
            Overrides = new Dictionary<string, FieldOverride> { ["customer_type"] = new() { Label = "Kind" } },
        };

        var view = await db.Customization.SaveAsync("Customer", customization, null, TestUsers.SystemManager);
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = 'tabCustomer' AND column_name = 'custom_phone'"));
        var id = (string)(await db.Documents.InsertAsync("Customer", Values(("customer_name", "Acme"), ("custom_phone", "555")), TestUsers.Admin))["name"]!;

        // "Restart": a fresh registry, the stored customisation applied again.
        var fresh = TestMetas.Registry();
        var failures = await new CustomizationManager(fresh, db.CustomizationStore, db.SchemaManager, new PermissionService(db.Overrides), db.Clock).LoadAsync();
        Assert.Empty(failures);
        Assert.Contains(fresh.Get("Customer").Fields, f => f.Name == "custom_phone");
        Assert.Equal("Kind", fresh.Get("Customer").Fields.Single(f => f.Name == "customer_type").Label);

        await Assert.ThrowsAsync<ConflictException>(() => db.Customization.SaveAsync("Customer", new Customization(), null, TestUsers.SystemManager)); // stale
        await db.Customization.ResetAsync("Customer", TestUsers.SystemManager);
        Assert.False((await db.Documents.GetAsync("Customer", id, TestUsers.Admin)).ContainsKey("custom_phone")); // hidden, not dropped
        Assert.Equal("555", await ReadPhoneAsync(db, id));
        Assert.NotNull(view.Modified);
    }

    private static async Task<string?> ReadPhoneAsync(PgStack db, string id)
    {
        await using var command = db.DataSource.CreateCommand($"SELECT \"custom_phone\" FROM \"tabCustomer\" WHERE \"name\" = '{id}'");
        return (string?)await command.ExecuteScalarAsync();
    }
}
