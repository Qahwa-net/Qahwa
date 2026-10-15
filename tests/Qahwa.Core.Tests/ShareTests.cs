using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Sharing one document with one person. Customer: Sales User may only use their own; Nobody has no role at all.</summary>
public class ShareTests
{
    private readonly TestEngine _engine = new();

    private async Task EnsureUsersAsync(params UserContext[] users)
    {
        foreach (var user in users)
            await _engine.Documents.InsertAsync("User", Values(("email", user.User), ("full_name", user.User)), TestUsers.Admin);
    }

    private async Task<string> CustomerBySalesAAsync(string name = "Acme")
    {
        var doc = await _engine.Documents.InsertAsync("Customer", Values(("customer_name", name)), TestUsers.SalesA);
        return (string)doc["name"]!;
    }

    [Fact]
    public async Task A_read_share_lets_a_person_with_no_role_open_just_that_document()
    {
        await EnsureUsersAsync(TestUsers.Nobody);
        var id = await CustomerBySalesAAsync();
        var other = await CustomerBySalesAAsync("Other");

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Customer", id, TestUsers.Nobody));
        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, read: true, write: false, TestUsers.SalesA);

        var doc = await _engine.Documents.GetAsync("Customer", id, TestUsers.Nobody);
        Assert.Equal("Acme", doc["customer_name"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Customer", other, TestUsers.Nobody));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Customer", id, Values(("credit_limit", 1)), TestUsers.Nobody));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Customer", id, TestUsers.Nobody));
    }

    [Fact]
    public async Task A_write_share_lets_them_edit_but_not_delete_or_see_protected_levels()
    {
        await EnsureUsersAsync(TestUsers.Nobody);
        var id = await CustomerBySalesAAsync();

        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, read: false, write: true, TestUsers.SalesA);

        var updated = await _engine.Documents.UpdateAsync("Customer", id, Values(("credit_limit", 5)), TestUsers.Nobody);
        Assert.Equal(5m, updated["credit_limit"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Customer", id, TestUsers.Nobody));
    }

    [Fact]
    public async Task Lists_show_shared_documents_to_people_with_no_role_and_add_them_for_own_only_roles()
    {
        await EnsureUsersAsync(TestUsers.Nobody, TestUsers.SalesB);
        var mine = await CustomerBySalesAAsync("A's");
        await CustomerBySalesAAsync("A's other");
        var bs = (string)(await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "B's")), TestUsers.SalesB))["name"]!;

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Customer", new ListRequest(), TestUsers.Nobody));

        await _engine.ShareManager.SetAsync("Customer", mine, TestUsers.Nobody.User, true, false, TestUsers.SalesA);
        await _engine.ShareManager.SetAsync("Customer", mine, TestUsers.SalesB.User, true, false, TestUsers.SalesA);

        var nobody = (await _engine.Documents.ListAsync("Customer", new ListRequest(), TestUsers.Nobody)).Data.Select(r => r["customer_name"]).ToList();
        var b = (await _engine.Documents.ListAsync("Customer", new ListRequest(), TestUsers.SalesB)).Data.Select(r => (string)r["customer_name"]!).Order().ToList();

        Assert.Equal(["A's"], nobody);
        Assert.Equal(["A's", "B's"], b);
        Assert.Equal(2, (await _engine.Documents.ListAsync("Customer", new ListRequest(), TestUsers.SalesB)).Total);
        Assert.NotNull(bs);
    }

    [Fact]
    public async Task The_meta_is_available_to_someone_who_only_has_a_share()
    {
        await EnsureUsersAsync(TestUsers.Nobody);
        var id = await CustomerBySalesAAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetMetaViewAsync("Customer", TestUsers.Nobody));
        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, true, false, TestUsers.SalesA);

        var view = await _engine.Documents.GetMetaViewAsync("Customer", TestUsers.Nobody);
        Assert.True(view.Permissions.Read);
        Assert.False(view.Permissions.Write);
        Assert.False(view.Permissions.Create);
        Assert.Empty(view.Meta.Permissions);
    }

    [Fact]
    public async Task Changing_and_removing_a_share_takes_effect_and_deleting_the_document_removes_its_shares()
    {
        await EnsureUsersAsync(TestUsers.Nobody);
        var id = await CustomerBySalesAAsync();

        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, true, false, TestUsers.SalesA);
        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, true, true, TestUsers.SalesA);
        var shares = await _engine.ShareManager.ListAsync("Customer", id, TestUsers.SalesA);
        var only = Assert.Single(shares);
        Assert.True(only.Write);

        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, false, false, TestUsers.SalesA);
        Assert.Empty(await _engine.ShareManager.ListAsync("Customer", id, TestUsers.SalesA));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Customer", id, TestUsers.Nobody));

        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, true, false, TestUsers.SalesA);
        await _engine.Documents.DeleteAsync("Customer", id, TestUsers.Manager);
        Assert.Empty(await _engine.Shares.RowsForDocumentAsync("Customer", id));
    }

    [Fact]
    public async Task Only_people_who_can_edit_a_document_through_their_roles_can_share_it()
    {
        await EnsureUsersAsync(TestUsers.Nobody, TestUsers.SalesB);
        var id = await CustomerBySalesAAsync();

        // Sales B owns nothing here, so an own-only role is not enough.
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, true, false, TestUsers.SalesB));

        // A person who was only given write access by a share cannot pass it on.
        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.Nobody.User, true, true, TestUsers.SalesA);
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.ShareManager.SetAsync("Customer", id, TestUsers.SalesB.User, true, false, TestUsers.Nobody));

        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.SalesB.User, true, false, TestUsers.Manager);
        await _engine.ShareManager.SetAsync("Customer", id, TestUsers.SalesB.User, true, false, TestUsers.Admin);
    }

    [Fact]
    public async Task Shares_need_a_real_user_and_do_not_reveal_documents_the_sharer_cannot_see()
    {
        var id = await CustomerBySalesAAsync();

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.ShareManager.SetAsync("Customer", id, "ghost@example.com", true, false, TestUsers.SalesA));
        Assert.Contains(ex.Errors, e => e.Field == "user");
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _engine.ShareManager.SetAsync("Customer", "NOPE", "ghost@example.com", true, false, TestUsers.Admin));
    }

    [Fact]
    public async Task User_permissions_still_apply_to_shared_documents()
    {
        await EnsureUsersAsync(TestUsers.Nobody);
        await _engine.Documents.InsertAsync("Territory", Values(("territory_name", "North")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Territory", Values(("territory_name", "South")), TestUsers.Admin);
        var south = (string)(await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "S"), ("territory", "South")), TestUsers.Admin))["name"]!;
        await _engine.Documents.InsertAsync("User Permission", Values(("user", TestUsers.Nobody.User), ("allow", "Territory"), ("for_value", "North"), ("apply_to_all_doctypes", true)), TestUsers.Admin);

        await _engine.ShareManager.SetAsync("Customer", south, TestUsers.Nobody.User, true, false, TestUsers.Admin);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Customer", south, TestUsers.Nobody));
    }
}
