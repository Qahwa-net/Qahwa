using Microsoft.Extensions.Options;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class SessionTests
{
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemorySessionStore _store = new();
    private readonly AuthOptions _options = new() { AbsoluteSessionDays = 2 };
    private readonly SessionService _sessions;

    public SessionTests() => _sessions = new SessionService(_store, Options.Create(_options), _clock);

    [Fact]
    public async Task A_session_is_valid_for_its_person_only_until_it_is_revoked()
    {
        var id = await _sessions.StartAsync("ann@example.com", "10.0.0.1", "Firefox");

        Assert.Equal(32, id.Length);
        Assert.True(await _sessions.IsValidAsync(id, "ann@example.com"));
        Assert.False(await _sessions.IsValidAsync(id, "bob@example.com")); // someone else's id is no good
        Assert.False(await _sessions.IsValidAsync("nope", "ann@example.com"));
        Assert.False(await _sessions.IsValidAsync(null, "ann@example.com"));
        Assert.False(await _sessions.IsValidAsync(new string('a', 500), "ann@example.com"));

        Assert.True(await _sessions.RevokeAsync("ann@example.com", id));
        Assert.False(await _sessions.IsValidAsync(id, "ann@example.com"));
        Assert.False(await _sessions.RevokeAsync("ann@example.com", id)); // already gone
    }

    [Fact]
    public async Task Nobody_can_end_someone_elses_session()
    {
        var annId = await _sessions.StartAsync("ann@example.com", null, null);

        Assert.False(await _sessions.RevokeAsync("bob@example.com", annId));
        Assert.True(await _sessions.IsValidAsync(annId, "ann@example.com"));
    }

    [Fact]
    public async Task A_session_ends_at_its_absolute_limit_however_active_it_is()
    {
        var id = await _sessions.StartAsync("ann@example.com", null, null);

        _clock.Advance(TimeSpan.FromDays(1));
        Assert.True(await _sessions.IsValidAsync(id, "ann@example.com")); // active, touched
        _clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        Assert.False(await _sessions.IsValidAsync(id, "ann@example.com"));
        Assert.Empty(await _sessions.ListAsync("ann@example.com"));
    }

    [Fact]
    public async Task Listing_shows_only_live_sessions_and_revoking_others_keeps_the_current_one()
    {
        var first = await _sessions.StartAsync("ann@example.com", "1.1.1.1", "Chrome");
        _clock.Advance(TimeSpan.FromMinutes(1));
        var second = await _sessions.StartAsync("ann@example.com", "2.2.2.2", "Safari");
        var other = await _sessions.StartAsync("bob@example.com", null, null);

        var list = await _sessions.ListAsync("ann@example.com");
        Assert.Equal([second, first], list.Select(s => s.Id)); // newest first
        Assert.Equal("Safari", list[0].Agent);

        Assert.Equal(1, await _sessions.RevokeOthersAsync("ann@example.com", second));
        Assert.Equal([second], (await _sessions.ListAsync("ann@example.com")).Select(s => s.Id));
        Assert.True(await _sessions.IsValidAsync(other, "bob@example.com")); // another person is untouched

        Assert.Equal(1, await _sessions.RevokeAllForAsync("ann@example.com"));
        Assert.Empty(await _sessions.ListAsync("ann@example.com"));
    }

    [Fact]
    public async Task What_the_browser_sends_is_kept_short_and_without_control_characters()
    {
        var id = await _sessions.StartAsync("ann@example.com", "1.2.3.4\r\nX-Evil: 1", new string('A', 1000) + "\0\n");

        var stored = (await _store.GetAsync(id))!;

        Assert.Equal(SessionService.MaxAgentLength, stored.Agent!.Length);
        Assert.DoesNotContain('\0', stored.Agent);
        Assert.DoesNotContain('\n', stored.Address!);
    }
}
