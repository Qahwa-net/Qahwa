using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class RealtimeBackplaneTests
{
    private readonly TestEngine _engine = new();
    private readonly RealtimeHub _hubA = new();
    private readonly RealtimeHub _hubB = new();
    private readonly RealtimeBackplane _a;
    private readonly RealtimeBackplane _b;

    public RealtimeBackplaneTests()
    {
        _a = new RealtimeBackplane(_engine.Registry, _engine.Repository, _hubA, _engine.Clock);
        _b = new RealtimeBackplane(_engine.Registry, _engine.Repository, _hubB, _engine.Clock);
    }

    private static RealtimeEvent? Next(RealtimeHub.Subscription s) => s.Reader.TryRead(out var m) ? m : null;

    [Fact]
    public async Task An_event_on_one_server_reaches_the_connections_of_the_other_once()
    {
        using var onA = _hubA.Subscribe("ann@example.com")!;
        using var onB = _hubB.Subscribe("ann@example.com")!;
        using var watchingB = _hubB.Subscribe("bob@example.com", "Order", "o1")!;
        await _b.PollAsync(); // a server that has just started notes where the table ends
        await _a.PollAsync();

        _a.Publish(new RealtimeEvent("notification", "ann@example.com"));
        _a.Publish(new RealtimeEvent("doc", "ann@example.com", "Order", "o1", Deleted: true));
        await _a.FlushAsync();
        var onB1 = await _b.PollAsync();
        var onA1 = await _a.PollAsync(); // its own events are not delivered a second time

        Assert.Equal(2, onB1);
        Assert.Equal(0, onA1);
        Assert.Equal("notification", Next(onB)!.Type);
        Assert.Equal("Order", Next(watchingB)!.DocType);
        Assert.True(Next(onA)!.Type == "notification" && Next(onA) is null); // once, from the local publish
        Assert.Equal(0, await _b.PollAsync()); // nothing is delivered twice
    }

    [Fact]
    public async Task A_server_that_starts_later_does_not_replay_old_events_and_old_rows_are_removed()
    {
        await _a.PollAsync();
        _a.Publish(new RealtimeEvent("notification", "ann@example.com"));
        await _a.FlushAsync();

        using var late = _hubB.Subscribe("ann@example.com")!;
        Assert.Equal(0, await _b.PollAsync()); // first poll: only notes where the table ends
        Assert.Null(Next(late));

        ((TestClock)_engine.Clock).Advance(RealtimeBackplane.Retention + TimeSpan.FromSeconds(1));
        await _b.PollAsync();
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Realtime Event"), new ListQuery { Limit = 10 }));
    }

    [Fact]
    public async Task Events_stay_in_order_and_publishing_with_nothing_to_flush_is_harmless()
    {
        using var onB = _hubB.Subscribe("ann@example.com", "Order", "o1")!;
        await _b.PollAsync();
        Assert.Equal(0, await _a.FlushAsync());

        for (var i = 0; i < 12; i++)
            _a.Publish(new RealtimeEvent("doc", $"u{i}@example.com", "Order", "o1"));
        await _a.FlushAsync();
        await _b.PollAsync();

        var users = new List<string>();
        while (Next(onB) is { } m)
            users.Add(m.User);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"u{i}@example.com"), users);
    }
}
