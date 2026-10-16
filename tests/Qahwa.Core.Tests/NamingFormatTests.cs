using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class NamingFormatTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public NamingFormatTests()
    {
        _engine.Clock.Set(new DateTimeOffset(2026, 3, 7, 10, 0, 0, TimeSpan.Zero));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Bill", "autoname": "BILL-{YYYY}-{MM}-.####", "fields": [ { "name": "who", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Visit", "autoname": "{branch}-{YY}{DD}-.###", "fields": [ { "name": "branch", "type": "Data" }, { "name": "note", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task<string> NameAsync(string doctype, params (string, object?)[] values) =>
        (string)(await _engine.Documents.InsertAsync(doctype, Values(values), Ann))["name"]!;

    [Fact]
    public async Task Dates_in_the_pattern_give_each_period_its_own_counter()
    {
        Assert.Equal("BILL-2026-03-0001", await NameAsync("Bill", ("who", "a")));
        Assert.Equal("BILL-2026-03-0002", await NameAsync("Bill", ("who", "b")));

        _engine.Clock.Set(new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("BILL-2026-04-0001", await NameAsync("Bill", ("who", "c")));
        _engine.Clock.Set(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("BILL-2027-01-0001", await NameAsync("Bill", ("who", "d")));
    }

    [Fact]
    public async Task A_field_in_the_pattern_gives_each_value_its_own_counter_and_only_safe_characters_survive()
    {
        Assert.Equal("RIY-2607-001", await NameAsync("Visit", ("branch", "RIY")));
        Assert.Equal("RIY-2607-002", await NameAsync("Visit", ("branch", "RIY")));
        Assert.Equal("JED-2607-001", await NameAsync("Visit", ("branch", "JED")));
        Assert.Equal("ABC-2607-001", await NameAsync("Visit", ("branch", " a b!c ".ToUpperInvariant())));

        var hostile = await NameAsync("Visit", ("branch", "x'); DROP TABLE tabVisit; --"));
        Assert.StartsWith("xDROPTABLEtabVisit--", hostile); // spaces, quotes, brackets and semicolons are dropped
        Assert.DoesNotContain("'", hostile);
        Assert.DoesNotContain(" ", hostile);
        Assert.DoesNotContain(";", hostile);
    }

    [Fact]
    public async Task The_field_is_required_and_must_leave_something_to_use()
    {
        var empty = await Assert.ThrowsAsync<DocumentValidationException>(() => NameAsync("Visit", ("note", "x")));
        Assert.Equal("branch", empty.Errors.Single().Field);
        var symbols = await Assert.ThrowsAsync<DocumentValidationException>(() => NameAsync("Visit", ("branch", "!!! ???")));
        Assert.Equal("branch", symbols.Errors.Single().Field);
        Assert.Equal("ABCDEFGHIJKLMNOPQRSTUVWXYZABCD-2607-001", await NameAsync("Visit", ("branch", "ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGH"))); // long values are cut to 30
    }

    [Theory]
    [InlineData("BILL-{YYYY}-.####", true)]
    [InlineData("{who}-.###", true)]
    [InlineData("{YYYY}{MM}{DD}-.#####", true)]
    [InlineData("A/{YY}-.##", true)]
    [InlineData("{nosuchfield}-.###", false)]
    [InlineData("{}-.###", false)]
    [InlineData("{who-.###", false)]
    [InlineData("{yyyy}-.###", false)] // a lower-case word is a field name, and there is no such field
    [InlineData("{who}.###", true)]
    [InlineData("BILL-{{who}}-.###", false)]
    [InlineData("BILL-{who;drop}-.###", false)]
    [InlineData("BILL-{YYYY}-.#", true)]
    [InlineData("BILL-{YYYY}-.###########", false)]
    public void Patterns_are_checked_when_a_doctype_is_defined(string rule, bool ok)
    {
        var json = $$"""{ "name": "Probe", "autoname": "{{rule.Replace("\"", "\\\"")}}", "fields": [ { "name": "who", "type": "Data" } ] }""";

        if (ok)
            Assert.Equal(rule, DocTypeLoader.Parse(json).Autoname);
        else
            Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Fact]
    public async Task The_series_page_shows_the_counters_that_came_out_of_a_pattern_under_its_doctype()
    {
        await NameAsync("Bill", ("who", "a"));
        await NameAsync("Visit", ("branch", "RIY"));
        var manager = new NamingSeriesManager(_engine.Registry, _engine.Repository, _engine.Permissions);

        var list = await manager.ListAsync(TestUsers.SystemManager);

        var bill = list.Single(s => s.Prefix == "BILL-2026-03-");
        Assert.Equal(1, bill.Current);
        Assert.Equal(4, bill.Digits);
        Assert.Equal(["Bill"], bill.DocTypes);
        Assert.Equal(["Visit"], list.Single(s => s.Prefix == "RIY-2607-").DocTypes);
        Assert.DoesNotContain(list, s => s.Prefix.Contains('{')); // a pattern is not a counter

        await manager.SetAsync("BILL-2026-03-", 99, TestUsers.SystemManager);
        Assert.Equal("BILL-2026-03-0100", await NameAsync("Bill", ("who", "z")));
        await Assert.ThrowsAsync<NotFoundException>(() => manager.SetAsync("BILL-1999-01-", 5, TestUsers.SystemManager)); // not a counter that exists
    }

    [Fact]
    public void The_prefix_regex_matches_only_what_a_pattern_can_produce()
    {
        var regex = new System.Text.RegularExpressions.Regex(NamingService.PrefixRegex("BILL-{YYYY}-{MM}-"));

        Assert.Matches(regex, "BILL-2026-03-");
        Assert.DoesNotMatch(regex, "BILL-26-03-");
        Assert.DoesNotMatch(regex, "BILL-2026-3-");
        Assert.DoesNotMatch(regex, "XBILL-2026-03-");
        Assert.Matches(new System.Text.RegularExpressions.Regex(NamingService.PrefixRegex("{branch}-")), "RIY-");
    }
}
