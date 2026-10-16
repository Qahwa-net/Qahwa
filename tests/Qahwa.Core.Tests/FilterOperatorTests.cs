using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class FilterOperatorTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public FilterOperatorTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Task", "autoname": "TK-.####", "fields": [
                { "name": "title", "type": "Data" }, { "name": "status", "type": "Data" }, { "name": "amount", "type": "Float" },
                { "name": "due", "type": "Date" }, { "name": "seen", "type": "Datetime" }, { "name": "worker", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Clock.Set(new DateTimeOffset(2026, 3, 18, 10, 0, 0, TimeSpan.Zero)); // a Wednesday
    }

    private async Task AddAsync(string title, string? status = null, double? amount = null, string? due = null, string? seen = null, string? worker = null) =>
        await _engine.Documents.InsertAsync("Task", Values(("title", title), ("status", status), ("amount", amount), ("due", due), ("seen", seen), ("worker", worker)), Ann);

    private async Task<string[]> TitlesAsync(params (string Field, string Op, object? Value)[] filters) =>
        (await _engine.Documents.ListAsync("Task", new ListRequest
        {
            Filters = filters.Select(f => new RawFilter(f.Field, f.Op, f.Value)).ToList(), Fields = ["title"], OrderBy = "title", Descending = false, Limit = 100,
        }, Ann)).Data.Select(r => (string)r["title"]!).ToArray();

    [Fact]
    public async Task Not_like_and_not_in_keep_the_rows_with_no_value()
    {
        await AddAsync("a", status: "Open");
        await AddAsync("b", status: "Closed");
        await AddAsync("c"); // no status at all

        Assert.Equal(["b", "c"], await TitlesAsync(("status", "not in", new[] { "Open" })));
        Assert.Equal(["a", "b", "c"], await TitlesAsync(("status", "not in", Array.Empty<string>())));
        Assert.Equal(["a", "c"], await TitlesAsync(("status", "not like", "%clos%")));
        Assert.Equal(["a", "b", "c"], await TitlesAsync(("title", "NOT LIKE", "z%"))); // the operator's case does not matter
    }

    [Fact]
    public async Task Between_includes_both_ends_and_works_for_numbers_and_dates()
    {
        await AddAsync("n1", amount: 1);
        await AddAsync("n5", amount: 5);
        await AddAsync("n9", amount: 9);
        await AddAsync("d1", due: "2026-03-01");
        await AddAsync("d2", due: "2026-03-10");
        await AddAsync("d3", due: "2026-03-20");

        Assert.Equal(["n1", "n5"], await TitlesAsync(("amount", "between", new object[] { 1, 5 })));
        Assert.Equal(["d1", "d2"], await TitlesAsync(("due", "between", new[] { "2026-03-01", "2026-03-10" })));
    }

    [Fact]
    public async Task A_moment_filtered_up_to_a_day_includes_the_whole_day()
    {
        await AddAsync("early", seen: "2026-03-10T00:00:00Z");
        await AddAsync("late", seen: "2026-03-10T23:59:59Z");
        await AddAsync("next", seen: "2026-03-11T00:00:00Z");

        Assert.Equal(["early", "late"], await TitlesAsync(("seen", "between", new[] { "2026-03-10", "2026-03-10" })));
    }

    [Theory]
    [InlineData("today", new[] { "d-wed" })]
    [InlineData("yesterday", new[] { "d-tue" })]
    [InlineData("this week", new[] { "d-mon", "d-tue", "d-wed", "d-sun" })]
    [InlineData("last week", new[] { "d-lastsun" })]
    [InlineData("next week", new[] { "d-nextmon" })]
    [InlineData("last 7 days", new[] { "d-mon", "d-tue", "d-wed", "d-lastsun" })]
    [InlineData("this month", new[] { "d-mon", "d-tue", "d-wed", "d-sun", "d-lastsun", "d-nextmon", "d-march1" })]
    [InlineData("last month", new[] { "d-feb" })]
    [InlineData("this year", new[] { "d-mon", "d-tue", "d-wed", "d-sun", "d-lastsun", "d-nextmon", "d-march1", "d-feb", "d-jan" })]
    [InlineData("last year", new[] { "d-2025" })]
    [InlineData("this_quarter", new[] { "d-mon", "d-tue", "d-wed", "d-sun", "d-lastsun", "d-nextmon", "d-march1", "d-feb", "d-jan" })]
    public async Task Timespans_are_relative_to_today(string span, string[] expected)
    {
        // Today is Wednesday 18 March 2026; weeks start on Monday.
        foreach (var (title, day) in new[]
                 {
                     ("d-mon", "2026-03-16"), ("d-tue", "2026-03-17"), ("d-wed", "2026-03-18"), ("d-sun", "2026-03-22"), ("d-lastsun", "2026-03-15"),
                     ("d-nextmon", "2026-03-23"), ("d-march1", "2026-03-01"), ("d-feb", "2026-02-20"), ("d-jan", "2026-01-05"), ("d-2025", "2025-12-31"),
                 })
            await AddAsync(title, due: day);

        var found = await TitlesAsync(("due", "timespan", span));

        Assert.Equal(expected.Order(), found.Order());
    }

    [Fact]
    public async Task A_timespan_on_a_moment_covers_whole_days()
    {
        await AddAsync("start", seen: "2026-03-18T00:00:00Z");
        await AddAsync("end", seen: "2026-03-18T23:59:59Z");
        await AddAsync("before", seen: "2026-03-17T23:59:59Z");
        await AddAsync("after", seen: "2026-03-19T00:00:00Z");

        Assert.Equal(["end", "start"], await TitlesAsync(("seen", "timespan", "today")));
    }

    [Fact]
    public async Task The_words_for_me_and_today_mean_the_person_asking_and_the_current_day()
    {
        await AddAsync("mine", worker: "ann@example.com", due: "2026-03-18");
        await AddAsync("theirs", worker: "bob@example.com", due: "2026-03-18");
        await AddAsync("later", worker: "ann@example.com", due: "2026-03-30");

        Assert.Equal(["later", "mine"], await TitlesAsync(("worker", "=", "__user")));
        Assert.Equal(["theirs"], await TitlesAsync(("worker", "!=", "__user"), ("due", "=", "__today")));
        Assert.Equal(["later", "mine"], await TitlesAsync(("worker", "in", new[] { "__user", "nobody@example.com" })));
    }

    [Fact]
    public async Task Only_the_exact_words_are_replaced()
    {
        await AddAsync("literal", worker: "__username");

        Assert.Equal(["literal"], await TitlesAsync(("worker", "=", "__username")));
        Assert.Empty(await TitlesAsync(("worker", "=", "__user ")));
    }

    [Theory]
    [InlineData("title", "between", "x")] // not a range type
    [InlineData("amount", "between", "5")] // one value, not two
    [InlineData("amount", "timespan", "today")] // not a date
    [InlineData("due", "timespan", "fortnight")] // unknown
    [InlineData("due", "timespan", "")]
    [InlineData("due", "between", null)]
    public async Task Wrong_uses_are_a_clear_validation_error(string field, string op, object? value)
    {
        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => TitlesAsync((field, op, value)));

        Assert.Equal(field, error.Errors.Single().Field);
    }

    [Fact]
    public async Task Hostile_values_are_only_data_and_unknown_operators_are_refused()
    {
        await AddAsync("safe", status: "x");

        Assert.Equal(["safe"], await TitlesAsync(("status", "not in", new[] { "'); DROP TABLE tabTask; --" })));
        Assert.Equal(["safe"], await TitlesAsync(("title", "not like", "'; DELETE FROM tabTask; --")));
        await Assert.ThrowsAsync<DocumentValidationException>(() => TitlesAsync(("status", "regex", ".*")));
        await Assert.ThrowsAsync<DocumentValidationException>(() => TitlesAsync(("status", "between; DROP", new[] { "a", "b" })));
        await Assert.ThrowsAsync<DocumentValidationException>(() => TitlesAsync(("amount", "between", new[] { "1; DROP TABLE x", "2" })));
    }

    [Fact]
    public void Every_named_span_is_known_and_the_boundaries_are_right()
    {
        var wednesday = new DateOnly(2026, 3, 18);
        foreach (var name in FilterExpansion.Timespans)
            Assert.NotNull(FilterExpansion.Span(name, wednesday));

        Assert.Equal((new DateOnly(2026, 3, 16), new DateOnly(2026, 3, 22)), FilterExpansion.Span("this week", wednesday));
        Assert.Equal((new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)), FilterExpansion.Span("last month", wednesday));
        Assert.Equal((new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)), FilterExpansion.Span("this quarter", wednesday));
        Assert.Equal((new DateOnly(2025, 10, 1), new DateOnly(2025, 12, 31)), FilterExpansion.Span("last quarter", wednesday));
        Assert.Equal((new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30)), FilterExpansion.Span("next month", wednesday));
        Assert.Equal((new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 18)), FilterExpansion.Span("last 7 days", wednesday));
        Assert.Equal((new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 31)), FilterExpansion.Span("this month", new DateOnly(2024, 3, 31)));
        Assert.Equal((new DateOnly(2024, 2, 1), new DateOnly(2024, 2, 29)), FilterExpansion.Span("last month", new DateOnly(2024, 3, 31))); // a leap year
        Assert.Null(FilterExpansion.Span("whenever", wednesday));
        Assert.Null(FilterExpansion.Span(null, wednesday));
    }
}
