using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ReminderTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Outsider"]);

    public ReminderTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "autoname": "MEMO-.####", "fields": [ { "name": "subject", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
    }

    private async Task UserAsync(UserContext person)
    {
        foreach (var role in person.Roles)
            if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), role))
                await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
        foreach (var role in person.Roles)
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
    }

    private async Task<string> MemoAsync() =>
        (string)(await _engine.Documents.InsertAsync("Memo", Values(("subject", "Renew the contract")), Ann))["name"]!;

    private string In(TimeSpan span) => _engine.Clock.GetUtcNow().Add(span).ToString("O");

    [Fact]
    public async Task A_reminder_is_sent_once_when_its_time_comes()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();

        var reminder = await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(2)), "Call them"), Ann);
        Assert.Equal("Pending", reminder.Status);

        Assert.Equal((0, 0), await _engine.Reminders.RunDueAsync()); // not yet
        _engine.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal((1, 0), await _engine.Reminders.RunDueAsync());
        Assert.Equal((0, 0), await _engine.Reminders.RunDueAsync()); // and not again

        var inbox = await _engine.Notifications.ListAsync(Ann, unreadOnly: false, limit: 10, before: null);
        var told = Assert.Single(inbox);
        Assert.Equal("Reminder: Call them", told.Subject);
        Assert.Empty(await _engine.Reminders.ListPendingAsync(Ann));
        Assert.Equal("Sent", Assert.Single(await _engine.Reminders.ForDocumentAsync("Memo", memo, Ann)).Status);
    }

    [Fact]
    public async Task A_reminder_without_a_note_names_the_document()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromMinutes(5))), Ann);
        _engine.Clock.Advance(TimeSpan.FromMinutes(6));

        await _engine.Reminders.RunDueAsync();

        Assert.Equal($"Reminder: Memo {memo}", Assert.Single(await _engine.Notifications.ListAsync(Ann, unreadOnly: false, limit: 10, before: null)).Subject);
    }

    [Fact]
    public async Task A_cancelled_reminder_is_never_sent()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        var reminder = await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(1))), Ann);

        await _engine.Reminders.CancelAsync(reminder.Id, Ann);
        _engine.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal((0, 0), await _engine.Reminders.RunDueAsync());
        Assert.Empty(await _engine.Notifications.ListAsync(Ann, unreadOnly: false, limit: 10, before: null));
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Reminders.CancelAsync(reminder.Id, Ann));
    }

    [Fact]
    public async Task Nobody_else_can_see_or_cancel_a_reminder_or_set_one_on_a_document_they_cannot_read()
    {
        await UserAsync(Ann);
        await UserAsync(Bob);
        var memo = await MemoAsync();
        var reminder = await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(1))), Ann);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Reminders.CancelAsync(reminder.Id, Bob));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(1))), Bob));
        Assert.Empty(await _engine.Reminders.ListPendingAsync(Bob));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(1))), UserContext.Anonymous));
    }

    [Fact]
    public async Task A_reminder_for_someone_who_can_no_longer_read_the_document_says_nothing()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(1))), Ann);
        await _engine.Documents.DeleteAsync("Memo", memo, Ann);
        _engine.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal((0, 1), await _engine.Reminders.RunDueAsync());
        Assert.Empty(await _engine.Notifications.ListAsync(Ann, unreadOnly: false, limit: 10, before: null));
    }

    [Theory]
    [InlineData("tomorrow-ish")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_time_that_is_not_a_time_is_refused(string? text)
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(text), Ann));
        Assert.Equal("remindAt", ex.Errors[0].Field);
    }

    [Fact]
    public async Task The_time_must_be_ahead_but_not_too_far()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromMinutes(-1))), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromDays(400))), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromDays(1)), new string('x', 501)), Ann));
    }

    [Fact]
    public async Task A_person_can_have_only_so_many_reminders_waiting()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        for (var i = 0; i < ReminderService.MaxPending; i++)
            await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromHours(1 + i))), Ann);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(In(TimeSpan.FromDays(300))), Ann));
    }

    [Fact]
    public async Task A_time_with_an_offset_is_kept_as_the_same_moment()
    {
        await UserAsync(Ann);
        var memo = await MemoAsync();
        var local = _engine.Clock.GetUtcNow().AddHours(5).ToOffset(TimeSpan.FromHours(3)).ToString("O");

        var reminder = await _engine.Reminders.CreateAsync("Memo", memo, new ReminderRequest(local), Ann);

        Assert.Equal(_engine.Clock.GetUtcNow().AddHours(5).UtcDateTime, reminder.RemindAt);
    }
}
