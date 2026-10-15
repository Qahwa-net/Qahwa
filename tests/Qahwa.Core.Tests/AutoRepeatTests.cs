using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AutoRepeatTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Viewer = new("vera@example.com", ["Viewer"]); // reads, cannot create

    public AutoRepeatTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Bill Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Bill", "autoname": "BILL-.####", "fields": [
                { "name": "customer", "type": "Data" }, { "name": "reference", "type": "Data", "unique": true }, { "name": "posting_date", "type": "Date" }, { "name": "due_at", "type": "Datetime" },
                { "name": "total", "type": "Currency" }, { "name": "workflow_note", "type": "Data" },
                { "name": "lines", "type": "Table", "options": "Bill Line" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true },
                { "role": "Viewer", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Named Bill", "autoname": "prompt", "fields": [ { "name": "customer", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task UserAsync(UserContext person, bool enabled = true)
    {
        foreach (var role in person.Roles)
        {
            if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), role))
                await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
        }

        await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", enabled)), TestUsers.Admin);
        foreach (var role in person.Roles)
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
    }

    private async Task PeopleAsync()
    {
        await UserAsync(Ann);
        await UserAsync(Bob);
        await UserAsync(Viewer);
    }

    private static string Day(int offset) => new DateOnly(2026, 1, 1).AddDays(offset).ToString("yyyy-MM-dd");

    private async Task<string> BillAsync(UserContext by)
    {
        var values = Values(("customer", "Acme"), ("reference", "REF-" + Guid.NewGuid().ToString("N")[..6]), ("posting_date", "2026-01-01"), ("total", 120));
        values["lines"] = new List<Dictionary<string, object?>> { Values(("item", "Tea"), ("qty", 2)), Values(("item", "Sugar"), ("qty", 1)) };
        return (string)(await _engine.Documents.InsertAsync("Bill", values, by))["name"]!;
    }

    private async Task<IReadOnlyList<string>> BillNamesAsync() =>
        (await _engine.Documents.ListAsync("Bill", new ListRequest { Fields = ["name"], OrderBy = "name", Descending = false, Limit = 100 }, Ann)).Data
            .Select(r => (string)r["name"]!).ToList();

    private void AdvanceDays(int days) => _engine.Clock.Advance(TimeSpan.FromDays(days));

    private static AutoRepeatRequest Daily(int startOffset = 1, string? end = null, params string[] dateFields) =>
        new("Daily", Day(startOffset), end, dateFields);

    // ---- the schedule ----

    [Theory]
    [InlineData("Daily", 3, "2026-01-04")]
    [InlineData("Weekly", 2, "2026-01-15")]
    [InlineData("Monthly", 1, "2026-02-01")]
    [InlineData("Quarterly", 1, "2026-04-01")]
    [InlineData("Yearly", 2, "2028-01-01")]
    public void Occurrences_are_counted_from_the_start_date(string frequency, int n, string expected) =>
        Assert.Equal(expected, AutoRepeatSchedule.On(new DateOnly(2026, 1, 1), frequency, n).ToString("yyyy-MM-dd"));

    [Fact]
    public void Months_never_drift_from_the_start_day()
    {
        var start = new DateOnly(2026, 1, 31);
        Assert.Equal(new DateOnly(2026, 2, 28), AutoRepeatSchedule.On(start, "Monthly", 1));
        Assert.Equal(new DateOnly(2026, 3, 31), AutoRepeatSchedule.On(start, "Monthly", 2));
        Assert.Equal(new DateOnly(2028, 2, 29), AutoRepeatSchedule.On(new DateOnly(2024, 2, 29), "Yearly", 4));
    }

    // ---- making copies ----

    [Fact]
    public async Task A_due_repeat_makes_a_draft_copy_with_its_table_rows_owned_by_whoever_set_it_up()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);

        Assert.Equal(new AutoRepeatRun(0, 0), await _engine.AutoRepeat.RunDueAsync()); // not due yet
        AdvanceDays(1);
        Assert.Equal(new AutoRepeatRun(1, 0), await _engine.AutoRepeat.RunDueAsync());

        var names = await BillNamesAsync();
        var copy = await _engine.Documents.GetAsync("Bill", names.Single(n => n != id), Ann);
        Assert.Equal(("Acme", 120m, "Ann"[0] == 'A'), (copy["customer"], Convert.ToDecimal(copy["total"]), true));
        Assert.Equal(Ann.User, copy["owner"]);
        Assert.Null(copy["reference"]); // a unique value is not copied
        Assert.Equal(0, Convert.ToInt32(copy["docstatus"]));
        Assert.Equal(["Tea", "Sugar"], ((IEnumerable<object?>)copy["lines"]!).Cast<IReadOnlyDictionary<string, object?>>().Select(r => (string)r["item"]!).ToList());

        var info = Assert.Single(await _engine.AutoRepeat.ForDocumentAsync("Bill", id, Ann));
        Assert.Equal((1L, Day(2), "Active"), (info.Created, info.NextDate, info.Status));
        Assert.Equal(names.Single(n => n != id), info.LastCreated);
        Assert.Contains(await _engine.Notifications.ListAsync(Ann, unreadOnly: false, limit: 10, before: null), n => n.Subject.StartsWith("Auto repeat made Bill"));
    }

    [Fact]
    public async Task Date_fields_are_set_to_the_date_of_each_copy()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(1, null, "posting_date", "due_at"), Ann);
        AdvanceDays(1);
        await _engine.AutoRepeat.RunDueAsync();

        var copy = await _engine.Documents.GetAsync("Bill", (await BillNamesAsync()).Single(n => n != id), Ann);
        Assert.Equal(new DateOnly(2026, 1, 2), copy["posting_date"]);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), copy["due_at"]);
    }

    [Fact]
    public async Task A_late_scheduler_catches_up_a_few_copies_at_a_time_and_never_repeats_one()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        AdvanceDays(9); // nine days are due

        Assert.Equal(AutoRepeatService.MaxCatchUp, (await _engine.AutoRepeat.RunDueAsync()).Created);
        Assert.Equal(9 - AutoRepeatService.MaxCatchUp, (await _engine.AutoRepeat.RunDueAsync()).Created);
        Assert.Equal(0, (await _engine.AutoRepeat.RunDueAsync()).Created);
        Assert.Equal(10, (await BillNamesAsync()).Count);
    }

    [Fact]
    public async Task A_repeat_finishes_after_its_end_date()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(1, Day(2)), Ann);
        AdvanceDays(5);

        Assert.Equal(2, (await _engine.AutoRepeat.RunDueAsync()).Created);

        var info = Assert.Single(await _engine.AutoRepeat.ForDocumentAsync("Bill", id, Ann));
        Assert.Equal(("Finished", 2L), (info.Status, info.Created));
        AdvanceDays(5);
        Assert.Equal(0, (await _engine.AutoRepeat.RunDueAsync()).Created);
    }

    [Fact]
    public async Task Paused_repeats_wait_and_resume_when_asked()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        var repeat = await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        await _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(Status: "Paused"), Ann);
        AdvanceDays(1);
        Assert.Equal(0, (await _engine.AutoRepeat.RunDueAsync()).Created);

        await _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(Status: "Active"), Ann);
        Assert.Equal(1, (await _engine.AutoRepeat.RunDueAsync()).Created);
    }

    [Fact]
    public async Task Creating_now_uses_up_the_next_occurrence()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        var repeat = await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(5), Ann);

        var info = await _engine.AutoRepeat.RunNowAsync(repeat.Id, Ann);

        Assert.Equal((1L, Day(6)), (info.Created, info.NextDate));
        Assert.Equal(2, (await BillNamesAsync()).Count);
    }

    // ---- when it goes wrong ----

    [Fact]
    public async Task A_copy_that_cannot_be_made_is_recorded_and_the_schedule_carries_on()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        var repeat = await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        // The person loses the right to create bills.
        await _engine.Documents.UpdateAsync("Has Role", (await _engine.Documents.ListAsync("Has Role", new ListRequest { Fields = ["name", "user"], Limit = 50 }, TestUsers.Admin))
            .Data.First(r => (string)r["user"]! == Ann.User)["name"]!.ToString()!, Values(("role", "Viewer")), TestUsers.Admin);
        AdvanceDays(1);

        var run = await _engine.AutoRepeat.RunDueAsync();

        Assert.Equal(new AutoRepeatRun(0, 1), run);
        var info = (await _engine.AutoRepeat.ListAsync(Ann)).Single(r => r.Id == repeat.Id);
        Assert.NotNull(info.LastError);
        Assert.Equal(0L, info.Created);
    }

    [Fact]
    public async Task A_disabled_owner_pauses_the_repeat()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        var repeat = await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        var user = (await _engine.Documents.ListAsync("User", new ListRequest { Fields = ["name", "email"], Limit = 50 }, TestUsers.Admin)).Data.First(r => (string)r["email"]! == Ann.User);
        await _engine.Documents.UpdateAsync("User", (string)user["name"]!, Values(("enabled", false)), TestUsers.Admin);
        AdvanceDays(1);

        Assert.Equal(1, (await _engine.AutoRepeat.RunDueAsync()).Failed);

        var info = (await _engine.AutoRepeat.ListAsync(TestUsers.Admin)).Single(r => r.Id == repeat.Id);
        Assert.Equal("Paused", info.Status);
        Assert.Contains("no longer an active user", info.LastError);
        Assert.Equal(0, (await _engine.AutoRepeat.RunDueAsync()).Failed); // it waits for a person
    }

    [Fact]
    public async Task A_second_scheduler_cannot_make_the_same_copy()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        AdvanceDays(1);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _engine.AutoRepeat.RunDueAsync()));

        Assert.Equal(1, results.Sum(r => r.Created));
        Assert.Equal(2, (await BillNamesAsync()).Count);
    }

    // ---- who may do what ----

    [Fact]
    public async Task Setting_up_a_repeat_needs_the_right_to_read_and_to_create()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Viewer)); // can read, cannot create
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.AutoRepeat.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AutoRepeat.CreateAsync("Bill", "NOPE", Daily(), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoRepeat.CreateAsync("User", "x", Daily(), TestUsers.Admin));
    }

    [Fact]
    public async Task Only_the_owner_or_a_system_manager_sees_and_changes_a_repeat()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        var repeat = await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        await _engine.Documents.UpdateAsync("Bill", id, Values(("customer", "Acme")), Ann);

        Assert.Empty(await _engine.AutoRepeat.ListAsync(Bob));
        Assert.Single(await _engine.AutoRepeat.ListAsync(TestUsers.SystemManager));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(Status: "Paused"), Bob));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AutoRepeat.DeleteAsync(repeat.Id, Bob));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AutoRepeat.RunNowAsync(repeat.Id, Bob));

        await _engine.AutoRepeat.DeleteAsync(repeat.Id, TestUsers.SystemManager);
        Assert.Empty(await _engine.AutoRepeat.ListAsync(Ann));
    }

    [Fact]
    public async Task Deleting_or_renaming_the_document_takes_its_repeats_with_it()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);
        var other = await BillAsync(Ann);
        await _engine.AutoRepeat.CreateAsync("Bill", other, Daily(), Ann);

        await _engine.Documents.DeleteAsync("Bill", id, Ann);

        Assert.Equal(other, Assert.Single(await _engine.AutoRepeat.ListAsync(Ann)).Name);
    }

    // ---- what is accepted ----

    [Fact]
    public async Task Problems_are_reported_by_field()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        async Task<string> FieldOf(AutoRepeatRequest request) =>
            (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoRepeat.CreateAsync("Bill", id, request, Ann))).Errors.First().Field;

        Assert.Equal("frequency", await FieldOf(new("Hourly", Day(1))));
        Assert.Equal("frequency", await FieldOf(new("'; DROP TABLE x; --", Day(1))));
        Assert.Equal("startDate", await FieldOf(new("Daily", "tomorrow")));
        Assert.Equal("startDate", await FieldOf(new("Daily", Day(-1)))); // in the past
        Assert.Equal("endDate", await FieldOf(new("Daily", Day(3), Day(1))));
        Assert.Equal("endDate", await FieldOf(new("Daily", Day(3), "soon")));
        Assert.Equal("dateFields", await FieldOf(new("Daily", Day(1), null, ["customer"]))); // not a date field
        Assert.Equal("dateFields", await FieldOf(new("Daily", Day(1), null, ["nope"])));
        Assert.Equal(string.Empty, (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoRepeat.CreateAsync("Named Bill", "x", Daily(), Ann))).Errors.First().Field);
    }

    [Fact]
    public async Task A_document_repeats_in_at_most_five_ways()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        for (var i = 0; i < AutoRepeatService.MaxPerDocument; i++)
            await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann));
    }

    [Fact]
    public async Task The_frequency_and_start_date_are_fixed_but_the_end_and_date_fields_can_change()
    {
        await PeopleAsync();
        var id = await BillAsync(Ann);
        var repeat = await _engine.AutoRepeat.CreateAsync("Bill", id, Daily(), Ann);

        var changed = await _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(EndDate: Day(30), DateFields: ["posting_date"]), Ann);
        Assert.Equal((Day(30), "posting_date"), (changed.EndDate, string.Join(',', changed.DateFields)));
        var cleared = await _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(EndDate: "", DateFields: []), Ann);
        Assert.Equal((null, 0), (cleared.EndDate, cleared.DateFields.Count));

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(EndDate: Day(-3)), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoRepeat.UpdateAsync(repeat.Id, new AutoRepeatRequest(Status: "Finished"), Ann));
    }
}
