using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>User Permissions: restrict a user to some values of a linked DocType (Customer.territory, Territory itself).</summary>
public class UserPermissionTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Rep = new("rep@example.com", ["Sales Manager"]);

    public UserPermissionTests()
    {
        // Territory needs to be readable and creatable by the manager role for these scenarios.
        _engine.Overrides.Set("Territory", [new DocPermMeta { Role = "Sales Manager", Read = true, Write = true, Create = true, Delete = true }]);
    }

    private readonly HashSet<string> _users = [];

    private async Task AllowAsync(string user, string allow, string value, bool all = true, string? only = null)
    {
        if (_users.Add(user))
            await _engine.Documents.InsertAsync("User", Values(("email", user), ("full_name", user)), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User Permission", Values(
            ("user", user), ("allow", allow), ("for_value", value),
            ("apply_to_all_doctypes", all), ("applicable_for", only)), TestUsers.SystemManager);
    }

    private async Task<string> CustomerAsync(string name, string? territory)
    {
        var doc = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", name), ("territory", territory)), TestUsers.Admin);
        return (string)doc["name"]!;
    }

    private async Task SeedAsync()
    {
        await _engine.Documents.InsertAsync("Territory", Values(("territory_name", "North")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Territory", Values(("territory_name", "South")), TestUsers.Admin);
    }

    private async Task<List<string>> CustomerNamesAsync(IUserContext user) =>
        (await _engine.Documents.ListAsync("Customer", new ListRequest { Limit = 100 }, user)).Data
            .Select(r => (string)r["customer_name"]!).Order().ToList();

    [Fact]
    public async Task A_restricted_user_only_sees_documents_linked_to_their_values_and_those_with_none()
    {
        await SeedAsync();
        await CustomerAsync("In North", "North");
        await CustomerAsync("In South", "South");
        await CustomerAsync("Nowhere", null);
        await AllowAsync(Rep.User, "Territory", "North");

        Assert.Equal(["In North", "Nowhere"], await CustomerNamesAsync(Rep));
        Assert.Equal(["In North", "In South", "Nowhere"], await CustomerNamesAsync(TestUsers.Admin));
        Assert.Equal(1, (await _engine.Documents.ListAsync("Customer", new ListRequest { Filters = [new("territory", "=", "North")] }, Rep)).Total);
    }

    [Fact]
    public async Task The_restricted_doctype_itself_is_limited_by_name()
    {
        await SeedAsync();
        await AllowAsync(Rep.User, "Territory", "North");

        var visible = (await _engine.Documents.ListAsync("Territory", new ListRequest(), Rep)).Data.Select(r => r["name"]).ToList();

        Assert.Equal(["North"], visible);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Territory", "South", Rep));
        await _engine.Documents.GetAsync("Territory", "North", Rep);
    }

    [Fact]
    public async Task Documents_outside_the_restriction_cannot_be_read_changed_or_deleted()
    {
        await SeedAsync();
        var south = await CustomerAsync("In South", "South");
        await AllowAsync(Rep.User, "Territory", "North");

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Customer", south, Rep));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Customer", south, Values(("credit_limit", 5)), Rep));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Customer", south, Rep));
    }

    [Fact]
    public async Task A_restricted_user_cannot_link_to_a_value_they_are_not_allowed()
    {
        await SeedAsync();
        await AllowAsync(Rep.User, "Territory", "North");

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("Customer", Values(("customer_name", "X"), ("territory", "South")), Rep));
        Assert.Contains(ex.Errors, e => e.Field == "territory");

        var ok = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "Y"), ("territory", "North")), Rep);
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Customer", (string)ok["name"]!, Values(("territory", "South")), Rep));
    }

    [Fact]
    public async Task Several_values_and_other_users_are_independent()
    {
        await SeedAsync();
        await CustomerAsync("In North", "North");
        await CustomerAsync("In South", "South");
        await AllowAsync(Rep.User, "Territory", "North");
        await AllowAsync(Rep.User, "Territory", "South");
        await AllowAsync("other@example.com", "Territory", "South");

        Assert.Equal(["In North", "In South"], await CustomerNamesAsync(Rep));
        Assert.Equal(["In South"], await CustomerNamesAsync(new UserContext("other@example.com", ["Sales Manager"])));
        Assert.Equal(2, (await CustomerNamesAsync(TestUsers.Manager)).Count); // no restriction rows: unaffected
    }

    [Fact]
    public async Task A_rule_can_apply_to_just_one_doctype()
    {
        await SeedAsync();
        await CustomerAsync("In South", "South");
        await AllowAsync(Rep.User, "Territory", "North", all: false, only: "Order");

        Assert.Equal(["In South"], await CustomerNamesAsync(Rep)); // not applicable to Customer
        await AllowAsync(Rep.User, "Territory", "North", all: false, only: "Customer");
        Assert.Empty(await CustomerNamesAsync(Rep));
    }

    [Fact]
    public async Task Only_system_managers_manage_user_permissions_and_they_are_never_restricted_themselves()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("User Permission", new ListRequest(), Rep));
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync("User Permission", Values(("user", Rep.User)), TestUsers.SystemManager));
    }
}
