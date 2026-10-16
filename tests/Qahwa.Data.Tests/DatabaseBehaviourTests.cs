using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>Behaviour every database product must share (these run on PostgreSQL and, with QAHWA_TEST_DB=sqlserver, on SQL Server).</summary>
public class DatabaseBehaviourTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly UserContext Clerk = new("c@example.com", ["Clerk"]);

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        var db = await postgres.CreateStackAsync();
        db.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Gadget", "autoname": "GD-.####", "fields": [
                { "name": "title", "type": "Data" }, { "name": "code", "type": "Data", "unique": true }, { "name": "note", "type": "Text" },
                { "name": "price", "type": "Currency" }, { "name": "made", "type": "Date" }, { "name": "at", "type": "Time" }, { "name": "seen", "type": "Datetime" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        await db.MigrateAsync();
        return db;
    }

    [SkippableFact]
    public async Task Series_numbers_are_unique_and_gapless_even_when_many_documents_are_made_at_once()
    {
        await using var db = await StackAsync();

        var made = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => db.Documents.InsertAsync("Gadget", Values(("title", $"g{i}")), Clerk)));

        var names = made.Select(m => (string)m["name"]!).Order().ToList();
        Assert.Equal(20, names.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"GD-{i:D4}"), names);
    }

    [SkippableFact]
    public async Task Any_number_of_empty_values_may_share_a_unique_field_but_a_repeated_value_is_refused()
    {
        await using var db = await StackAsync();

        await db.Documents.InsertAsync("Gadget", Values(("title", "a")), Clerk);
        await db.Documents.InsertAsync("Gadget", Values(("title", "b")), Clerk);
        await db.Documents.InsertAsync("Gadget", Values(("title", "c"), ("code", "X1")), Clerk);

        await Assert.ThrowsAsync<ConflictException>(() => db.Documents.InsertAsync("Gadget", Values(("title", "d"), ("code", "X1")), Clerk));
        Assert.Equal(3, (await db.Documents.ListAsync("Gadget", new ListRequest(), Clerk)).Total);
    }

    [SkippableFact]
    public async Task Text_in_any_script_comes_back_as_it_went_in_and_searching_ignores_case()
    {
        await using var db = await StackAsync();
        const string arabic = "مقهى الصباح – قهوة ☕ 日本語 Ünïcödé";
        await db.Documents.InsertAsync("Gadget", Values(("title", arabic), ("note", arabic + new string('x', 5000))), Clerk);
        await db.Documents.InsertAsync("Gadget", Values(("title", "Acme Ltd")), Clerk);

        var rows = (await db.Documents.ListAsync("Gadget", new ListRequest { Fields = ["title", "note"], OrderBy = "title", Descending = false }, Clerk)).Data;

        Assert.Contains(rows, r => (string?)r["title"] == arabic && ((string)r["note"]!).Length == arabic.Length + 5000);
        var found = await db.Documents.ListAsync("Gadget", new ListRequest { Filters = [new RawFilter("title", "like", "%acme%")], Fields = ["title"] }, Clerk);
        Assert.Equal("Acme Ltd", Assert.Single(found.Data)["title"]);
        var arabicFound = await db.Documents.ListAsync("Gadget", new ListRequest { Filters = [new RawFilter("title", "like", "%الصباح%")], Fields = ["title"] }, Clerk);
        Assert.Single(arabicFound.Data);
    }

    [SkippableFact]
    public async Task Dates_times_money_and_moments_round_trip_exactly()
    {
        await using var db = await StackAsync();
        var created = await db.Documents.InsertAsync("Gadget", Values(("title", "t"), ("price", "1234.5678"), ("made", "2026-03-04"), ("at", "13:45:30"), ("seen", "2026-03-04T10:20:30.123456Z")), Clerk);

        var saved = await db.Documents.GetAsync("Gadget", (string)created["name"]!, Clerk);

        Assert.Equal(1234.5678m, saved["price"]);
        Assert.Equal(new DateOnly(2026, 3, 4), saved["made"]);
        Assert.Equal(new TimeOnly(13, 45, 30), saved["at"]);
        var seen = Assert.IsType<DateTime>(saved["seen"]);
        Assert.Equal(DateTimeKind.Utc, seen.Kind);
        Assert.Equal(new DateTime(2026, 3, 4, 10, 20, 30, DateTimeKind.Utc).AddTicks(1234560), seen);
        var modified = Assert.IsType<DateTime>(saved["modified"]);
        Assert.Equal(DateTimeKind.Utc, modified.Kind);
    }

    [SkippableFact]
    public async Task A_save_with_an_old_modified_time_is_a_conflict_and_a_current_one_is_not()
    {
        await using var db = await StackAsync();
        var created = await db.Documents.InsertAsync("Gadget", Values(("title", "v1")), Clerk);
        var name = (string)created["name"]!;
        db.Clock.Advance(TimeSpan.FromSeconds(2));

        var second = await db.Documents.UpdateAsync("Gadget", name, Values(("title", "v2"), ("modified", created["modified"])), Clerk);

        await Assert.ThrowsAsync<ConflictException>(() => db.Documents.UpdateAsync("Gadget", name, Values(("title", "v3"), ("modified", created["modified"])), Clerk));
        await db.Documents.UpdateAsync("Gadget", name, Values(("title", "v4"), ("modified", second["modified"])), Clerk);
    }

    [SkippableFact]
    public async Task Hostile_text_is_only_ever_data()
    {
        await using var db = await StackAsync();
        const string evil = "x'); DROP TABLE [tabGadget]; DROP TABLE \"tabGadget\"; --";
        await db.Documents.InsertAsync("Gadget", Values(("title", evil)), Clerk);

        var found = await db.Documents.ListAsync("Gadget", new ListRequest { Filters = [new RawFilter("title", "=", evil)], Fields = ["title"] }, Clerk);

        Assert.Equal(evil, Assert.Single(found.Data)["title"]);
        Assert.True(await db.TableExistsAsync("tabGadget"));
    }

    [SkippableFact]
    public async Task A_filter_with_far_too_many_values_is_a_clear_refusal_and_a_big_one_still_works()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("Gadget", Values(("title", "one")), Clerk);

        var big = Enumerable.Range(0, 1500).Select(i => (object)$"n{i}").Append("one").ToList();
        var ok = await db.Documents.ListAsync("Gadget", new ListRequest { Filters = [new RawFilter("title", "in", big)], Fields = ["title"] }, Clerk);
        Assert.Single(ok.Data);

        var tooMany = Enumerable.Range(0, db.Dialect.MaxParameters + 10).Select(i => (object)$"n{i}").ToList();
        await Assert.ThrowsAsync<FormatException>(() => db.Documents.ListAsync("Gadget", new ListRequest { Filters = [new RawFilter("title", "in", tooMany)], Fields = ["title"] }, Clerk));
    }
}
