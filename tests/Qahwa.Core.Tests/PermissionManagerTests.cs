using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class PermissionManagerTests
{
    private readonly TestEngine _engine = new();

    private static DocPermMeta Rule(string role, bool read = false, bool write = false, bool create = false, bool delete = false, bool ifOwner = false, int level = 0) =>
        new() { Role = role, Read = read, Write = write, Create = create, Delete = delete, IfOwner = ifOwner, PermLevel = level };

    private Task<PermissionView> SaveAsync(string doctype, IUserContext user, DateTime? expected, params DocPermMeta[] rules) =>
        _engine.PermissionManager.SaveAsync(doctype, rules, expected, user);

    [Fact]
    public async Task Only_system_managers_and_administrators_can_manage_permissions()
    {
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.PermissionManager.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.PermissionManager.ListAsync(TestUsers.Manager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.PermissionManager.GetAsync("Customer", TestUsers.SalesA));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => SaveAsync("Customer", TestUsers.Manager, null, Rule("Sales User", read: true)));

        Assert.NotEmpty(await _engine.PermissionManager.ListAsync(TestUsers.SystemManager));
    }

    [Fact]
    public async Task The_list_shows_which_doctypes_are_customised_and_leaves_out_child_tables()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Line", "isChildTable": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
        await SaveAsync("Territory", TestUsers.SystemManager, null, Rule("Sales User", read: true));

        var items = await _engine.PermissionManager.ListAsync(TestUsers.SystemManager);

        Assert.DoesNotContain(items, i => i.Name == "Line");
        var territory = items.Single(i => i.Name == "Territory");
        Assert.True(territory.Customized);
        Assert.Equal(1, territory.RuleCount);
        Assert.False(items.Single(i => i.Name == "Customer").Customized);
        Assert.Equal(DocTypeSource.Core, items.Single(i => i.Name == "User").Source);
    }

    [Fact]
    public async Task The_view_shows_the_rules_in_force_the_definition_s_defaults_and_where_fields_sit()
    {
        var view = await _engine.PermissionManager.GetAsync("Customer", TestUsers.SystemManager);

        Assert.False(view.Customized);
        Assert.Equal(2, view.Rules.Count);
        Assert.Equal(view.Rules, view.DefaultRules);
        Assert.Null(view.Modified);
        Assert.True(view.CanEdit);

        var core = await _engine.PermissionManager.GetAsync("User", TestUsers.SystemManager);
        Assert.False(core.CanEdit); // a System Manager may look, only an Administrator may change
    }

    [Fact]
    public async Task Customised_rules_take_effect_immediately_and_replace_the_definition_s_rules()
    {
        // Territory: only Sales Manager may read. Sales User is shut out.
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.SalesA));

        var view = await SaveAsync("Territory", TestUsers.SystemManager, null, Rule("Sales User", read: true));

        Assert.True(view.Customized);
        await _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.SalesA);                       // now allowed
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.Manager)); // replaced, not merged
        Assert.Contains("Territory", _engine.Documents.ListMetas(TestUsers.SalesA).Select(m => m.Name));
        Assert.DoesNotContain("Territory", _engine.Documents.ListMetas(TestUsers.Manager).Select(m => m.Name));
    }

    [Fact]
    public async Task Resetting_goes_back_to_the_definition()
    {
        await SaveAsync("Territory", TestUsers.SystemManager, null, Rule("Sales User", read: true));

        var view = await _engine.PermissionManager.ResetAsync("Territory", TestUsers.SystemManager);

        Assert.False(view.Customized);
        await _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.Manager);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.SalesA));
        Assert.Null(await _engine.PermissionStore.GetAsync("Territory"));
    }

    [Fact]
    public async Task An_empty_rule_list_means_only_administrators_can_use_the_doctype()
    {
        await SaveAsync("Territory", TestUsers.SystemManager, null);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.Manager));
        await _engine.Documents.ListAsync("Territory", new ListRequest(), TestUsers.Admin);
    }

    [Fact]
    public async Task Field_levels_can_be_granted_from_the_manager()
    {
        var meta = DocTypeLoader.Parse("""
            { "name": "Payroll", "fields": [ { "name": "who", "type": "Data" }, { "name": "pay", "type": "Currency", "permLevel": 1 } ],
              "permissions": [ { "role": "Sales User", "read": true, "write": true, "create": true } ] }
            """);
        _engine.Registry.Add(meta);
        await _engine.Documents.InsertAsync("Payroll", Values(("who", "A"), ("pay", 10)), TestUsers.Admin);

        Assert.DoesNotContain("pay", (await _engine.Documents.GetAsync("Payroll", (await _engine.Documents.ListAsync("Payroll", new ListRequest(), TestUsers.SalesA)).Data[0]["name"]!.ToString()!, TestUsers.SalesA)).Keys);

        await SaveAsync("Payroll", TestUsers.SystemManager, null,
            Rule("Sales User", read: true, write: true, create: true), Rule("Sales User", read: true, level: 1));

        var name = (string)(await _engine.Documents.ListAsync("Payroll", new ListRequest(), TestUsers.SalesA)).Data[0]["name"]!;
        Assert.Equal(10m, (await _engine.Documents.GetAsync("Payroll", name, TestUsers.SalesA))["pay"]);
    }

    [Fact]
    public async Task Rules_are_validated_with_paths()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync("Territory", TestUsers.SystemManager, null,
            Rule(" ", read: true),
            Rule("A", read: true), Rule("A", read: true),
            Rule("B", read: true, create: true, level: 2),
            Rule("C", read: true, level: 12)));

        var paths = ex.Errors.Select(e => e.Field).ToHashSet();
        Assert.Contains("rules[0].role", paths);
        Assert.Contains("rules[2].role", paths);
        Assert.Contains("rules[3].permLevel", paths);
        Assert.Contains("rules[4].permLevel", paths);
        Assert.Null(await _engine.PermissionStore.GetAsync("Territory")); // nothing saved
    }

    [Fact]
    public async Task Only_an_administrator_can_change_the_permissions_of_the_frameworks_own_doctypes()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            SaveAsync("User", TestUsers.SystemManager, null, Rule("Sales User", read: true, write: true)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.PermissionManager.ResetAsync("User", TestUsers.SystemManager));

        await SaveAsync("User", TestUsers.Admin, null, Rule("System Manager", read: true, write: true, create: true, delete: true), Rule("HR Lead", read: true));
        Assert.True((await _engine.PermissionManager.GetAsync("User", TestUsers.SystemManager)).Customized);
    }

    [Fact]
    public async Task Child_tables_have_no_permissions_of_their_own()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Line", "isChildTable": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.PermissionManager.GetAsync("Line", TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => SaveAsync("Line", TestUsers.Admin, null, Rule("X", read: true)));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.PermissionManager.GetAsync("Nope", TestUsers.Admin));
    }

    [Fact]
    public async Task Concurrent_edits_are_detected()
    {
        var first = await SaveAsync("Territory", TestUsers.SystemManager, null, Rule("Sales User", read: true));

        // Someone who did not see the first customisation tries to create theirs.
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync("Territory", TestUsers.SystemManager, null, Rule("X", read: true)));

        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await SaveAsync("Territory", TestUsers.SystemManager, first.Modified, Rule("Sales User", read: true, write: true));
        Assert.True(second.Modified > first.Modified);

        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync("Territory", TestUsers.SystemManager, first.Modified, Rule("Y", read: true)));
        Assert.Single((await _engine.PermissionManager.GetAsync("Territory", TestUsers.Admin)).Rules, r => r.Write);
    }

    [Fact]
    public async Task Customised_rules_survive_a_restart_and_unreadable_rows_are_skipped()
    {
        await SaveAsync("Territory", TestUsers.SystemManager, null, Rule("Sales User", read: true));
        var now = DateTime.UtcNow;
        await _engine.PermissionStore.SaveAsync(new StoredPermissions("Broken", "{ not json", now, "x"), null);

        var restarted = new PermissionOverrides();
        var manager = new PermissionManager(_engine.Registry, _engine.PermissionStore, restarted, _engine.Clock);
        var failures = await manager.LoadAsync();

        Assert.Single(failures);
        Assert.Equal("Sales User", Assert.Single(restarted.Get("Territory")!).Role);
        Assert.Null(restarted.Get("Broken"));
    }

    [Fact]
    public async Task Roles_are_trimmed_and_the_rules_round_trip()
    {
        await SaveAsync("Territory", TestUsers.SystemManager, null, Rule("  Sales User  ", read: true, ifOwner: true, level: 0));

        var rule = Assert.Single((await _engine.PermissionManager.GetAsync("Territory", TestUsers.Admin)).Rules);

        Assert.Equal("Sales User", rule.Role);
        Assert.True(rule.IfOwner);
        Assert.Equal("Sales User", Assert.Single(DocTypeLoader.ParseRules(DocTypeLoader.SerializeRules([rule]))).Role);
    }
}
