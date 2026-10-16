using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class GlobalSearchTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public GlobalSearchTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Patron", "titleField": "patron_name", "autoname": "PAT-.####", "fields": [ { "name": "patron_name", "type": "Data" }, { "name": "secret_note", "type": "Data", "permLevel": 1 } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Viewer", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Receipt", "autoname": "RCT-.####", "fields": [ { "name": "total", "type": "Float" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Hidden Thing", "titleField": "label", "autoname": "HT-.####", "fields": [ { "name": "label", "type": "Data", "permLevel": 1 } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Own Only", "titleField": "label", "autoname": "OO-.####", "fields": [ { "name": "label", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "ifOwner": true } ] }
            """));
    }

    private Task PatronAsync(string name, UserContext? who = null) => _engine.Documents.InsertAsync("Patron", Values(("patron_name", name)), who ?? Ann);

    [Fact]
    public async Task Documents_are_found_by_their_title_and_doctypes_by_their_name()
    {
        await PatronAsync("Acme Coffee Roasters");
        await PatronAsync("Brew House");
        await _engine.Documents.InsertAsync("Receipt", Values(("total", 5)), Ann);

        var found = await _engine.GlobalSearch.SearchAsync("coffee", Ann);

        Assert.Equal("Acme Coffee Roasters", Assert.Single(found.Documents).Title);
        Assert.Equal("Patron", found.Documents[0].DocType);
        Assert.Empty(found.DocTypes);

        var types = await _engine.GlobalSearch.SearchAsync("rec", Ann);
        Assert.Equal("Receipt", Assert.Single(types.DocTypes).Name);
        Assert.True(types.DocTypes[0].CanCreate);
    }

    [Fact]
    public async Task A_document_without_a_title_field_is_found_by_its_name_and_a_name_like_text_also_matches_names()
    {
        await _engine.Documents.InsertAsync("Receipt", Values(("total", 5)), Ann);
        await PatronAsync("Zed Corp");

        var byName = await _engine.GlobalSearch.SearchAsync("RCT-0001", Ann);
        Assert.Equal("RCT-0001", Assert.Single(byName.Documents).Name);

        var customerByName = await _engine.GlobalSearch.SearchAsync("pat-0001", Ann);
        Assert.Equal("Zed Corp", Assert.Single(customerByName.Documents).Title);
    }

    [Fact]
    public async Task People_only_find_what_they_may_see()
    {
        await _engine.Documents.InsertAsync("Receipt", Values(("total", 5)), Ann);
        await _engine.Documents.InsertAsync("Hidden Thing", Values(("label", "classified")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Own Only", Values(("label", "mine only")), Ann);
        await _engine.Documents.InsertAsync("Own Only", Values(("label", "mine too")), new UserContext("bob@example.com", ["Clerk"]));
        await PatronAsync("Visible Patron");

        var viewer = await _engine.GlobalSearch.SearchAsync("0001", Vic);
        Assert.Equal(["Patron"], viewer.Documents.Select(d => d.DocType)); // no Receipt access
        Assert.False((await _engine.GlobalSearch.SearchAsync("patron", Vic)).DocTypes.Single().CanCreate);

        Assert.Empty((await _engine.GlobalSearch.SearchAsync("classified", Ann)).Documents); // the title is above their permission level
        Assert.Equal(["mine only"], (await _engine.GlobalSearch.SearchAsync("mine", Ann)).Documents.Select(d => d.Title)); // own-only
        Assert.Empty((await _engine.GlobalSearch.SearchAsync("classified", new UserContext("nobody@example.com", []))).Documents);
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("[a-z]")]
    [InlineData("a\\")]
    [InlineData("100%")]
    public async Task Pattern_characters_are_looked_for_as_they_are(string text)
    {
        await PatronAsync("Plain name");
        await PatronAsync("Sale 100% off");
        await PatronAsync("under_score");

        var found = (await _engine.GlobalSearch.SearchAsync(text, Ann)).Documents.Select(d => d.Title ?? string.Empty).ToArray();

        Assert.DoesNotContain("Plain name", found); // a wildcard does not match everything
        if (text == "100%")
            Assert.Equal(["Sale 100% off"], found);
        if (text == "_")
            Assert.Equal(["under_score"], found);
        if (text == "%")
            Assert.Equal(["Sale 100% off"], found);
    }

    [Fact]
    public async Task Hostile_text_is_only_data_and_odd_input_is_refused_or_empty()
    {
        await PatronAsync("safe");

        Assert.Empty((await _engine.GlobalSearch.SearchAsync("'; DROP TABLE tabPatron; --", Ann)).Documents);
        Assert.Single((await _engine.GlobalSearch.SearchAsync("safe", Ann)).Documents);
        Assert.Empty((await _engine.GlobalSearch.SearchAsync("   ", Ann)).Documents);
        Assert.Empty((await _engine.GlobalSearch.SearchAsync(null, Ann)).Documents);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.GlobalSearch.SearchAsync(new string('a', 101), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.GlobalSearch.SearchAsync("a\r\nb", Ann));
    }

    [Fact]
    public async Task The_number_of_results_and_searches_is_limited()
    {
        for (var i = 0; i < 40; i++)
            await PatronAsync($"Match {i}");

        var found = await _engine.GlobalSearch.SearchAsync("match", Ann);

        Assert.Equal(GlobalSearchService.PerDocType, found.Documents.Count); // five per DocType, the most recent first
        for (var i = 1; i < GlobalSearchService.PerMinute; i++)
            await _engine.GlobalSearch.SearchAsync("match", Ann);
        await Assert.ThrowsAsync<RateLimitedException>(() => _engine.GlobalSearch.SearchAsync("match", Ann));
        Assert.NotNull(await _engine.GlobalSearch.SearchAsync("match", Vic)); // another person has their own allowance
    }
}
