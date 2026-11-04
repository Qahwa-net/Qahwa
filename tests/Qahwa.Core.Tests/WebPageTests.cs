using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Web pages: published content anyone can read without signing in (ADR-160).</summary>
public class WebPageTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("ann@example.com", ["Clerk"]);

    private Task PageAsync(string route, string title, bool published = true, string? content = "<p>Hello</p>", bool listed = true, string? summary = null, string? on = null) =>
        _engine.Documents.InsertAsync("Web Page", Values(("route", route), ("title", title), ("published", published), ("show_in_list", listed), ("summary", summary), ("published_on", on), ("content", content)), TestUsers.Admin);

    [Fact]
    public async Task A_published_page_is_read_by_its_address_with_cleaned_html()
    {
        await PageAsync("about-us", "About us", content: "<h2>Who we are</h2><p onclick=\"steal()\">Coffee</p><script>alert(1)</script><a href=\"javascript:alert(1)\">x</a>", summary: "Hi");

        var page = await _engine.WebPages.GetAsync("about-us");

        Assert.Equal(("About us", "Hi"), (page.Title, page.Summary));
        Assert.Contains("<h2>Who we are</h2>", page.Html, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", page.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("script", page.Html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", page.Html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_draft_and_an_unknown_or_odd_address_all_look_like_a_page_that_does_not_exist()
    {
        await PageAsync("secret-plans", "Plans", published: false);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebPages.GetAsync("secret-plans"));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebPages.GetAsync("nothing-here"));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebPages.GetAsync("../etc/passwd"));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebPages.GetAsync("Has Capitals"));
    }

    [Fact]
    public async Task The_index_lists_published_and_listed_pages_newest_first_and_nothing_else()
    {
        await PageAsync("old", "Old news", on: "2025-01-01");
        await PageAsync("new", "New news", on: "2026-03-01", summary: "Fresh");
        await PageAsync("hidden", "Unlisted", listed: false);
        await PageAsync("draft", "Draft", published: false);

        var index = await _engine.WebPages.IndexAsync();

        Assert.Equal(["new", "old"], index.Select(p => p.Route));
        Assert.Equal("Fresh", index[0].Summary);
        Assert.Equal(new DateOnly(2026, 3, 1), index[0].PublishedOn);
        Assert.NotNull((await _engine.WebPages.GetAsync("hidden")).Title);   // unlisted pages are still readable by their address
    }

    [Fact]
    public async Task Addresses_and_size_are_checked_and_only_system_managers_write_pages()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => PageAsync("Bad Address!", "x"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => PageAsync("big", "x", content: new string('a', HtmlSanitizer.MaxInputLength + 1)));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Web Page", Values(("route", "mine"), ("title", "Mine")), Clerk));
    }
}
