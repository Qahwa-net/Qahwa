using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AutoEmailReportTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Boss = new("boss@example.com", ["System Manager", "Clerk"]);
    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private string _report = string.Empty;

    public AutoEmailReportTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Sale", "fields": [ { "name": "customer", "type": "Data", "label": "Customer" }, { "name": "amount", "type": "Currency", "label": "Amount" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "report": true, "export": true } ] }
            """));
    }

    private async Task SetUpAsync(int sales = 3)
    {
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "System Manager")), TestUsers.Admin);
        foreach (var person in new[] { Boss, Clerk })
        {
            await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
            foreach (var role in person.Roles)
                await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
        }

        for (var i = 1; i <= sales; i++)
        {
            _engine.Clock.Advance(TimeSpan.FromSeconds(1));
            await _engine.Documents.InsertAsync("Sale", Values(("customer", $"Customer {i}"), ("amount", i * 10m)), Boss);
        }

        var created = await _engine.Reports.CreateAsync(new ReportRequest("Sales so far", true, new ReportDefinition { DocType = "Sale", Columns = ["customer", "amount"], Limit = 100 }), Boss);
        _report = created.Id;
    }

    private static AutoEmailReportRequest Request(string report, string recipients = "a@example.com, b@example.com") => new(report, recipients, "Daily", 8);

    private async Task<List<Dictionary<string, object?>>> QueueAsync() =>
        (await _engine.Repository.ListAsync(_engine.Registry.Get("Email Queue"), new ListQuery { Limit = 100, OrderBy = "recipient", Descending = false })).ToList();

    [Fact]
    public async Task A_due_report_is_emailed_once_to_every_recipient_as_a_text_table()
    {
        await SetUpAsync();
        var info = await _engine.AutoEmailReports.CreateAsync(Request(_report), Boss);
        Assert.True(info.NextSend > _engine.Clock.GetUtcNow().UtcDateTime);

        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(1));
        var (sent, failed) = await _engine.AutoEmailReports.RunDueAsync();
        var again = await _engine.AutoEmailReports.RunDueAsync();

        Assert.Equal((1, 0), (sent, failed));
        Assert.Equal((0, 0), again);
        var queue = await QueueAsync();
        Assert.Equal(["a@example.com", "b@example.com"], queue.Select(q => (string)q["recipient"]!));
        var body = (string)queue[0]["body"]!;
        Assert.Equal("Sales so far", queue[0]["subject"]);
        Assert.Contains("Customer | Amount", body);
        Assert.Contains("Customer 1 | 10", body);
        Assert.EndsWith("3 rows.", body);
        var listed = (await _engine.AutoEmailReports.ListAsync(Boss)).Single();
        Assert.Equal(1, listed.SentCount);
        Assert.Null(listed.LastError);
    }

    [Fact]
    public async Task Days_that_were_missed_send_one_email_not_one_per_day()
    {
        await SetUpAsync();
        await _engine.AutoEmailReports.CreateAsync(Request(_report, "a@example.com"), Boss);

        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(10));
        await _engine.AutoEmailReports.RunDueAsync();
        await _engine.AutoEmailReports.RunDueAsync();

        Assert.Single(await QueueAsync());
        Assert.True((await _engine.AutoEmailReports.ListAsync(Boss)).Single().NextSend > _engine.Clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task Only_the_rows_asked_for_are_sent_and_the_rest_are_counted()
    {
        await SetUpAsync(sales: 5);
        await _engine.AutoEmailReports.CreateAsync(Request(_report, "a@example.com") with { MaxRows = 2 }, Boss);

        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(1));
        await _engine.AutoEmailReports.RunDueAsync();

        var body = (string)Assert.Single(await QueueAsync())["body"]!;
        Assert.Contains("Showing the first 2 of 5 rows", body);
        Assert.Contains("Customer 5", body);
        Assert.Contains("Customer 4", body);
        Assert.DoesNotContain("Customer 3", body);
    }

    [Fact]
    public async Task A_report_runs_with_the_permissions_of_the_person_it_runs_as_and_stops_when_they_are_disabled()
    {
        await SetUpAsync();
        var info = await _engine.AutoEmailReports.CreateAsync(Request(_report, "a@example.com") with { RunAs = Clerk.User }, Boss);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report) with { RunAs = "nobody@example.com" }, Boss));

        await _engine.Documents.UpdateAsync("User", Clerk.User, Values(("enabled", false)), TestUsers.Admin);
        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(1));
        var (sent, failed) = await _engine.AutoEmailReports.RunDueAsync();

        Assert.Equal((0, 1), (sent, failed));
        Assert.Empty(await QueueAsync());
        Assert.Contains("no longer an enabled user", (await _engine.AutoEmailReports.ListAsync(Boss)).Single(i => i.Id == info.Id).LastError);
    }

    [Fact]
    public async Task Only_a_system_manager_can_set_these_up_and_the_input_is_checked()
    {
        await SetUpAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report), Clerk));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.AutoEmailReports.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request("NOPE"), Boss));
        foreach (var bad in new[] { "", "not-an-email", "a@example.com\nBcc: evil@example.com", "a@b.c, " + string.Join(", ", Enumerable.Range(0, 11).Select(i => $"u{i}@example.com")), "Name <a@example.com>" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report, bad), Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report) with { Frequency = "Hourly" }, Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report) with { SendHour = 24 }, Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report) with { MaxRows = 0 }, Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AutoEmailReports.CreateAsync(Request(_report) with { Subject = "a\r\nBcc: x" }, Boss));
    }

    [Fact]
    public async Task A_report_can_be_paused_changed_sent_now_and_deleted()
    {
        await SetUpAsync();
        var info = await _engine.AutoEmailReports.CreateAsync(Request(_report, "a@example.com"), Boss);

        var paused = await _engine.AutoEmailReports.UpdateAsync(info.Id, new AutoEmailReportRequest(null, null, null, null, Status: "Paused"), Boss);
        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(2));
        Assert.Equal("Paused", paused.Status);
        Assert.Equal((0, 0), await _engine.AutoEmailReports.RunDueAsync());

        Assert.Equal(1, await _engine.AutoEmailReports.SendNowAsync(info.Id, Boss));
        Assert.Single(await QueueAsync());
        await _engine.AutoEmailReports.DeleteAsync(info.Id, Boss);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AutoEmailReports.SendNowAsync(info.Id, Boss));
    }

    [Theory]
    [InlineData("Daily", 8, "2026-01-01T12:00:00Z", "2026-01-02T08:00:00Z")]
    [InlineData("Daily", 13, "2026-01-01T12:00:00Z", "2026-01-01T13:00:00Z")]
    [InlineData("Weekly", 8, "2026-01-01T12:00:00Z", "2026-01-08T08:00:00Z")]
    [InlineData("Monthly", 8, "2026-01-31T12:00:00Z", "2026-02-28T08:00:00Z")]
    public void The_next_time_is_always_in_the_future(string frequency, int hour, string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected, null, System.Globalization.DateTimeStyles.AdjustToUniversal), AutoEmailReportService.NextSlot(DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal), frequency, hour));

    [Fact]
    public void A_monthly_report_moves_a_month_at_a_time_from_its_last_slot()
    {
        var slot = new DateTime(2026, 1, 31, 8, 0, 0, DateTimeKind.Utc);
        var next = AutoEmailReportService.NextSlot(new DateTime(2026, 1, 31, 9, 0, 0, DateTimeKind.Utc), "Monthly", 8, slot);

        Assert.Equal(new DateTime(2026, 2, 28, 8, 0, 0, DateTimeKind.Utc), next);
    }
}
