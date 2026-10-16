using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class EmailDigestTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);

    public EmailDigestTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Task Item", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task SetUpAsync()
    {
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        foreach (var person in new[] { Ann, Bob })
        {
            await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", "Clerk")), TestUsers.Admin);
        }

        await _engine.Documents.InsertAsync("Task Item", Values(("title", "t1")), Bob);
    }

    private async Task<List<Dictionary<string, object?>>> QueueAsync() =>
        (await _engine.Repository.ListAsync(_engine.Registry.Get("Email Queue"), new ListQuery { Limit = 100 })).ToList();

    private void Days(int days) => ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(days));

    [Fact]
    public async Task A_person_who_turned_it_on_gets_their_unread_notifications_and_assignments_once_a_day()
    {
        await SetUpAsync();
        await _engine.Notifications.NotifyAsync(Ann.User, "Mention", "Task Item", "t1", Bob.User, "Bob mentioned you in t1");
        await _engine.Assignments.AssignAsync("Task Item", "t1", Ann.User, "Please check", null, null, Bob);
        var set = await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: true));
        Assert.True(set.Enabled);
        Assert.True(set.NextSend > _engine.Clock.GetUtcNow().UtcDateTime);

        Days(1);
        var (sent, _) = await _engine.EmailDigests.RunDueAsync();
        var again = await _engine.EmailDigests.RunDueAsync();

        Assert.Equal(1, sent);
        Assert.Equal((0, 0), again);
        var mail = Assert.Single(await QueueAsync());
        Assert.Equal(Ann.User, mail["recipient"]);
        Assert.Equal("Your daily Qahwa digest", mail["subject"]);
        var body = (string)mail["body"]!;
        Assert.Contains("Bob mentioned you in t1", body);
        Assert.Contains("Open assignments (1)", body);
        Assert.Contains("Please check", body);
        Assert.NotNull((await _engine.EmailDigests.GetAsync(Ann)).LastSent);
    }

    [Fact]
    public async Task Nothing_is_sent_when_there_is_nothing_to_say_and_a_missed_week_sends_one()
    {
        await SetUpAsync();
        await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: true));

        Days(1);
        Assert.Equal((0, 1), await _engine.EmailDigests.RunDueAsync());
        Assert.Empty(await QueueAsync());

        await _engine.Notifications.NotifyAsync(Ann.User, "Mention", "Task Item", "t1", Bob.User, "Bob mentioned you");
        Days(10);
        await _engine.EmailDigests.RunDueAsync();
        await _engine.EmailDigests.RunDueAsync();
        Assert.Single(await QueueAsync());
    }

    [Fact]
    public async Task It_goes_to_the_person_only_and_not_to_people_who_did_not_ask_for_it()
    {
        await SetUpAsync();
        await _engine.Notifications.NotifyAsync(Ann.User, "Mention", "Task Item", "t1", Bob.User, "For Ann");
        await _engine.Notifications.NotifyAsync(Bob.User, "Mention", "Task Item", "t1", Ann.User, "For Bob");
        await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: true));

        Days(1);
        await _engine.EmailDigests.RunDueAsync();

        var mail = Assert.Single(await QueueAsync());
        Assert.Equal(Ann.User, mail["recipient"]);
        Assert.DoesNotContain("For Bob", (string)mail["body"]!);
        Assert.False((await _engine.EmailDigests.GetAsync(Bob)).Enabled);
    }

    [Fact]
    public async Task Turning_it_off_or_disabling_the_account_stops_it_and_settings_are_checked()
    {
        await SetUpAsync();
        await _engine.Notifications.NotifyAsync(Ann.User, "Mention", "Task Item", "t1", Bob.User, "x");
        await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: true, Frequency: "Weekly", SendHour: 9));
        var off = await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: false));
        Assert.False(off.Enabled);
        Assert.Null(off.NextSend);
        Days(10);
        Assert.Equal((0, 0), await _engine.EmailDigests.RunDueAsync());

        await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: true));
        await _engine.Documents.UpdateAsync("User", Ann.User, Values(("enabled", false)), TestUsers.Admin);
        Days(10);
        Assert.Equal((0, 1), await _engine.EmailDigests.RunDueAsync());
        Assert.Empty(await QueueAsync());

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.EmailDigests.SetAsync(Bob, new EmailDigestRequest(Frequency: "Hourly")));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.EmailDigests.SetAsync(Bob, new EmailDigestRequest(SendHour: 24)));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.EmailDigests.SetAsync(new UserContext("Administrator", ["Administrator"]), new EmailDigestRequest(Enabled: true)));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.EmailDigests.GetAsync(UserContext.Anonymous));
    }

    [Fact]
    public async Task Long_or_odd_text_in_a_notification_cannot_break_the_email()
    {
        await SetUpAsync();
        await _engine.Notifications.NotifyAsync(Ann.User, "Mention", "Task Item", "t1", Bob.User, "line one\r\nBcc: evil@example.com\n" + new string('x', 1000));
        await _engine.EmailDigests.SetAsync(Ann, new EmailDigestRequest(Enabled: true));

        Days(1);
        await _engine.EmailDigests.RunDueAsync();

        var body = (string)Assert.Single(await QueueAsync())["body"]!;
        Assert.DoesNotContain("\r", body);
        Assert.DoesNotContain("\nBcc:", body);
        Assert.True(body.Length < 2000);
    }
}
