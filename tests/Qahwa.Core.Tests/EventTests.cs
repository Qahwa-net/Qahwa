using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class EventTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk", UserContext.AllRole]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk", UserContext.AllRole]);

    [Fact]
    public async Task A_person_keeps_their_own_events_and_others_do_not_see_them()
    {
        var mine = (string)(await _engine.Documents.InsertAsync("Event", Values(("subject", "Dentist"), ("starts_on", "2026-03-05T09:00:00Z")), Ann))["name"]!;

        Assert.Equal("Open", (await _engine.Documents.GetAsync("Event", mine, Ann))["status"]);
        Assert.Empty((await _engine.Documents.ListAsync("Event", new ListRequest { Limit = 10 }, Bob)).Data);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Event", mine, Bob));
    }

    [Fact]
    public async Task An_event_cannot_end_before_it_starts()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync(
            "Event", Values(("subject", "Backwards"), ("starts_on", "2026-03-05T10:00:00Z"), ("ends_on", "2026-03-05T09:00:00Z")), Ann));
        Assert.Contains(ex.Errors, e => e.Field == "ends_on");

        await _engine.Documents.InsertAsync("Event", Values(("subject", "Fine"), ("starts_on", "2026-03-05T10:00:00Z"), ("ends_on", "2026-03-05T11:00:00Z")), Ann);
        await _engine.Documents.InsertAsync("Event", Values(("subject", "No end"), ("starts_on", "2026-03-05T10:00:00Z")), Ann);
    }

    [Fact]
    public async Task An_event_can_have_a_reminder()
    {
        await _engine.Documents.InsertAsync("User", Values(("email", Ann.User), ("full_name", "Ann"), ("enabled", true)), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", Ann.User), ("role", "Clerk")), TestUsers.Admin);
        var id = (string)(await _engine.Documents.InsertAsync("Event", Values(("subject", "Dentist"), ("starts_on", "2026-03-05T09:00:00Z")), Ann))["name"]!;

        await _engine.Reminders.CreateAsync("Event", id, new ReminderRequest(_engine.Clock.GetUtcNow().AddHours(1).ToString("O")), Ann);
        _engine.Clock.Advance(TimeSpan.FromHours(2));

        Assert.Equal((1, 0), await _engine.Reminders.RunDueAsync());
    }
}
