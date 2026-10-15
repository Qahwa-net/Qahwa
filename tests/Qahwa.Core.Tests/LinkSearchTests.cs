using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Link fields: search by title or ID, friendly names for chosen values, and the Share panel's user picker.</summary>
public class LinkSearchTests
{
    private readonly TestEngine _engine = new();

    private async Task<string> CustomerAsync(string name, UserContext? by = null) =>
        (string)(await _engine.Documents.InsertAsync("Customer", Values(("customer_name", name)), by ?? TestUsers.Admin))["name"]!;

    [Fact]
    public async Task Search_matches_the_title_and_shows_it_instead_of_the_id()
    {
        var acme = await CustomerAsync("Acme Trading");
        await CustomerAsync("Globex");

        var found = await _engine.Documents.SearchLinkAsync("Customer", "acme", 10, TestUsers.Admin);

        var option = Assert.Single(found);
        Assert.Equal(acme, option.Value);
        Assert.Equal("Acme Trading", option.Label);
        Assert.Equal(acme, option.Description); // the ID is the small hint
    }

    [Fact]
    public async Task Search_also_matches_the_id_and_an_empty_search_gives_recent_documents()
    {
        var acme = await CustomerAsync("Acme");
        await CustomerAsync("Globex");

        Assert.Equal("Acme", Assert.Single(await _engine.Documents.SearchLinkAsync("Customer", acme, 10, TestUsers.Admin)).Label);
        Assert.Equal(2, (await _engine.Documents.SearchLinkAsync("Customer", "", 10, TestUsers.Admin)).Count);
        Assert.Empty(await _engine.Documents.SearchLinkAsync("Customer", "zzz", 10, TestUsers.Admin));
    }

    [Fact]
    public async Task A_doctype_without_a_title_shows_its_name_and_results_are_limited()
    {
        for (var i = 0; i < 30; i++)
            await _engine.Documents.InsertAsync("Territory", Values(("territory_name", $"T{i:00}")), TestUsers.Admin);

        var found = await _engine.Documents.SearchLinkAsync("Territory", "T", 500, TestUsers.Admin);

        Assert.Equal(DocumentService.MaxLinkResults, found.Count);
        Assert.All(found, o => Assert.Null(o.Description));
        Assert.Equal(found[0].Value, found[0].Label);
    }

    [Fact]
    public async Task Search_only_offers_documents_the_user_may_read()
    {
        await CustomerAsync("Mine", TestUsers.SalesA);
        await CustomerAsync("Theirs", TestUsers.SalesB);

        var found = await _engine.Documents.SearchLinkAsync("Customer", "", 10, TestUsers.SalesA);

        Assert.Equal(["Mine"], found.Select(o => o.Label));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.SearchLinkAsync("Customer", "", 10, TestUsers.Nobody));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.SearchLinkAsync("Nope", "", 10, TestUsers.Admin));
    }

    [Fact]
    public async Task Search_text_is_only_ever_a_parameter()
    {
        await CustomerAsync("Acme");

        var found = await _engine.Documents.SearchLinkAsync("Customer", "x' OR '1'='1\"; DROP TABLE tabCustomer;--", 10, TestUsers.Admin);

        Assert.Empty(found);
        Assert.Empty(await _engine.Documents.SearchLinkAsync("Customer", new string('a', 500), 10, TestUsers.Admin)); // overlong text is cut, not an error
    }

    [Fact]
    public async Task Titles_resolve_chosen_values_and_skip_what_cannot_be_read()
    {
        var acme = await CustomerAsync("Acme", TestUsers.SalesA);
        var theirs = await CustomerAsync("Theirs", TestUsers.SalesB);

        var titles = await _engine.Documents.LinkTitlesAsync("Customer", [acme, theirs, "", "MISSING"], TestUsers.SalesA);

        Assert.Equal("Acme", titles[acme].Label);
        Assert.False(titles.ContainsKey(theirs));
        Assert.False(titles.ContainsKey("MISSING"));
        Assert.Empty(await _engine.Documents.LinkTitlesAsync("Customer", [], TestUsers.SalesA));
    }

    // ---- share panel user picker ----

    private async Task UsersAsync()
    {
        foreach (var (email, name, enabled) in new[] { ("ann@example.com", "Ann Lee", true), ("bob@example.com", "Bob Stone", true), ("old@example.com", "Annie Gone", false) })
            await _engine.Documents.InsertAsync("User", Values(("email", email), ("full_name", name), ("enabled", enabled)), TestUsers.Admin);
    }

    [Fact]
    public async Task The_user_picker_searches_by_name_and_email_and_shows_names()
    {
        await UsersAsync();
        var customer = await CustomerAsync("Acme", TestUsers.SalesA);

        var byName = await _engine.ShareManager.SearchUsersAsync("Customer", customer, "ann", TestUsers.SalesA);
        var byEmail = await _engine.ShareManager.SearchUsersAsync("Customer", customer, "bob@", TestUsers.SalesA);

        var ann = Assert.Single(byName); // the disabled "Annie Gone" is not offered
        Assert.Equal("ann@example.com", ann.Value);
        Assert.Equal("Ann Lee", ann.Label);
        Assert.Equal("ann@example.com", ann.Description);
        Assert.Equal("Bob Stone", Assert.Single(byEmail).Label);
    }

    [Fact]
    public async Task The_user_picker_leaves_out_yourself_and_is_only_for_people_who_may_share()
    {
        await UsersAsync();
        await _engine.Documents.InsertAsync("User", Values(("email", TestUsers.SalesA.User), ("full_name", "Sales A")), TestUsers.Admin);
        var customer = await CustomerAsync("Acme", TestUsers.SalesA);

        var all = await _engine.ShareManager.SearchUsersAsync("Customer", customer, "", TestUsers.SalesA);
        Assert.DoesNotContain(all, o => o.Value == TestUsers.SalesA.User);
        Assert.NotEmpty(all);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ShareManager.SearchUsersAsync("Customer", customer, "", TestUsers.SalesB));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.ShareManager.SearchUsersAsync("Customer", customer, "", TestUsers.Nobody));
    }
}
