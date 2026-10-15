using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ProfileTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);

    public ProfileTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Quote", "module": "Sales",
              "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ]
            }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Ticket", "module": "Support",
              "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ]
            }
            """));
    }

    private async Task RolesAsync(params string[] names)
    {
        foreach (var name in names)
            await _engine.Documents.InsertAsync("Role", Values(("role_name", name)), TestUsers.Admin);
    }

    private async Task<List<string>> RolesOfAsync(string user) =>
        (await _engine.Documents.ListAsync("Has Role", new ListRequest { Filters = [new("user", "=", user)], Limit = 100 }, TestUsers.Admin))
            .Data.Select(r => (string)r["role"]!).Order().ToList();

    private Task ProfileAsync(string name, params string[] roles) =>
        _engine.Documents.InsertAsync("Role Profile", Values(("role_profile", name), ("roles", roles.Select(r => Values(("role", r))).ToArray())), TestUsers.Admin);

    // ---- role profiles ----

    [Fact]
    public async Task Choosing_a_role_profile_gives_the_user_exactly_its_roles()
    {
        await RolesAsync("Role A", "Role B", "Role C");
        await ProfileAsync("Team", "Role A", "Role B");
        await _engine.Documents.InsertAsync("User", Values(("email", "u@example.com"), ("full_name", "U")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", "u@example.com"), ("role", "Role C")), TestUsers.Admin);

        await _engine.Documents.UpdateAsync("User", "u@example.com", Values(("role_profile", "Team")), TestUsers.Admin);

        Assert.Equal(["Role A", "Role B"], await RolesOfAsync("u@example.com"));
    }

    [Fact]
    public async Task A_new_user_created_with_a_profile_gets_its_roles()
    {
        await RolesAsync("Role A");
        await ProfileAsync("Team", "Role A");

        await _engine.Documents.InsertAsync("User", Values(("email", "n@example.com"), ("full_name", "N"), ("role_profile", "Team")), TestUsers.Admin);

        Assert.Equal(["Role A"], await RolesOfAsync("n@example.com"));
    }

    [Fact]
    public async Task Changing_a_profile_s_roles_updates_everyone_who_has_it_and_nobody_else()
    {
        await RolesAsync("Role A", "Role B", "Role C");
        await ProfileAsync("Team", "Role A");
        await _engine.Documents.InsertAsync("User", Values(("email", "in@example.com"), ("full_name", "In"), ("role_profile", "Team")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", "out@example.com"), ("full_name", "Out")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", "out@example.com"), ("role", "Role C")), TestUsers.Admin);

        await _engine.Documents.UpdateAsync("Role Profile", "Team", Values(("roles", new[] { Values(("role", "Role B")) })), TestUsers.Admin);

        Assert.Equal(["Role B"], await RolesOfAsync("in@example.com"));
        Assert.Equal(["Role C"], await RolesOfAsync("out@example.com"));
    }

    [Fact]
    public async Task Only_system_managers_can_manage_profiles()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.InsertAsync("Role Profile", Values(("role_profile", "X")), Clerk));
        await _engine.Documents.InsertAsync("Role Profile", Values(("role_profile", "X")), TestUsers.SystemManager);
    }

    // ---- module profiles ----

    private async Task BlockSalesForClerkAsync()
    {
        await _engine.Documents.InsertAsync("Module Profile",
            Values(("module_profile", "No Sales"), ("blocked_modules", new[] { Values(("module", "Sales")) })), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", Clerk.User), ("full_name", "Clerk"), ("module_profile", "No Sales")), TestUsers.Admin);
    }

    [Fact]
    public async Task A_blocked_module_is_out_of_reach_everywhere()
    {
        var quote = (string)(await _engine.Documents.InsertAsync("Quote", Values(("title", "Q")), TestUsers.Admin))["name"]!;
        await BlockSalesForClerkAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Quote", new ListRequest(), Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Quote", quote, Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Quote", Values(("title", "N")), Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Quote", quote, Values(("title", "N")), Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Quote", quote, Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetMetaViewAsync("Quote", Clerk));
    }

    [Fact]
    public async Task Other_modules_other_users_and_administrators_are_unaffected()
    {
        await BlockSalesForClerkAsync();

        await _engine.Documents.InsertAsync("Ticket", Values(("title", "T")), Clerk);
        var names = (await _engine.Documents.ListMetasAsync(Clerk)).Select(m => m.Name).ToList();
        Assert.Contains("Ticket", names);
        Assert.DoesNotContain("Quote", names);

        var other = new UserContext("other@example.com", ["Clerk"]);
        await _engine.Documents.InsertAsync("Quote", Values(("title", "Q")), other);
        await _engine.Documents.InsertAsync("Quote", Values(("title", "A")), TestUsers.Admin);
        Assert.Contains("Quote", (await _engine.Documents.ListMetasAsync(other)).Select(m => m.Name));
    }

    [Fact]
    public async Task Unblocking_takes_effect_at_once()
    {
        await BlockSalesForClerkAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Quote", new ListRequest(), Clerk));

        await _engine.Documents.UpdateAsync("User", Clerk.User, Values(("module_profile", null)), TestUsers.Admin);

        await _engine.Documents.ListAsync("Quote", new ListRequest(), Clerk);
    }
}

public class ProfileAssignmentTests
{
    private readonly TestEngine _engine = new();

    [Fact]
    public async Task Choosing_a_role_profile_needs_the_right_to_assign_roles()
    {
        // HR may edit users, but only System Managers may create Has Role documents.
        _engine.Overrides.Set("User", [new DocPermMeta { Role = "HR", Read = true, Write = true, Create = true }]);
        var hr = new UserContext("hr@example.com", ["HR"]);
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "System Manager")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Role Profile", Values(("role_profile", "Bosses"), ("roles", new[] { Values(("role", "System Manager")) })), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", "w@example.com"), ("full_name", "W")), TestUsers.Admin);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("User", "w@example.com", Values(("role_profile", "Bosses")), hr));
        Assert.Contains(ex.Errors, e => e.Field == "role_profile");
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("User", Values(("email", "x@example.com"), ("full_name", "X"), ("role_profile", "Bosses")), hr));

        // Editing the user without touching the profile is still fine, and a System Manager may assign.
        await _engine.Documents.UpdateAsync("User", "w@example.com", Values(("full_name", "W2")), hr);
        await _engine.Documents.UpdateAsync("User", "w@example.com", Values(("role_profile", "Bosses")), TestUsers.Admin);
    }
}

public class ProfileEditingTests
{
    private readonly TestEngine _engine = new();

    [Fact]
    public async Task Editing_a_role_profile_needs_the_right_to_assign_roles()
    {
        _engine.Overrides.Set("Role Profile", [new DocPermMeta { Role = "HR", Read = true, Write = true, Create = true }]);
        var hr = new UserContext("hr@example.com", ["HR"]);

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Role Profile", Values(("role_profile", "Mine")), hr));
        await _engine.Documents.InsertAsync("Role Profile", Values(("role_profile", "Mine")), TestUsers.Admin);
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Role Profile", "Mine", Values(("roles", new[] { Values(("role", "Whatever")) })), hr));
    }
}
