using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class RealtimeTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public RealtimeTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Order", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data", "required": true }, { "name": "note", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
    }

    private static RealtimeEvent? Next(RealtimeHub.Subscription s) => s.Reader.TryRead(out var message) ? message : null;

    [Fact]
    public void An_event_reaches_only_the_connections_it_is_meant_for()
    {
        var hub = new RealtimeHub();
        using var annGlobal = hub.Subscribe("ann@example.com")!;
        using var bobGlobal = hub.Subscribe("bob@example.com")!;
        using var annWatching = hub.Subscribe("ann@example.com", "Order", "o1")!;
        using var bobOther = hub.Subscribe("bob@example.com", "Order", "o2")!;

        hub.Publish(new RealtimeEvent("notification", "ann@example.com"));
        hub.Publish(new RealtimeEvent("doc", "bob@example.com", "Order", "o1"));

        Assert.Equal("notification", Next(annGlobal)!.Type);
        Assert.Null(Next(annGlobal));
        Assert.Null(Next(bobGlobal));
        Assert.Equal("doc", Next(annWatching)!.Type);
        Assert.Null(Next(annWatching));
        Assert.Null(Next(bobOther));
    }

    [Fact]
    public void Connections_are_limited_per_person_and_freed_when_closed()
    {
        var hub = new RealtimeHub();
        var open = Enumerable.Range(0, RealtimeHub.MaxConnectionsPerUser).Select(_ => hub.Subscribe("ann@example.com")).ToList();

        Assert.All(open, s => Assert.NotNull(s));
        Assert.Null(hub.Subscribe("ann@example.com"));
        Assert.NotNull(hub.Subscribe("bob@example.com"));
        open[0]!.Dispose();
        Assert.NotNull(hub.Subscribe("ann@example.com"));
        Assert.Equal(RealtimeHub.MaxConnectionsPerUser + 1, hub.Connections);
    }

    [Fact]
    public void A_reader_that_never_reads_keeps_only_the_latest_events()
    {
        var hub = new RealtimeHub();
        using var slow = hub.Subscribe("ann@example.com")!;
        for (var i = 0; i < 1000; i++)
            hub.Publish(new RealtimeEvent("notification", "ann@example.com"));

        var count = 0;
        while (Next(slow) is not null)
            count++;
        Assert.InRange(count, 1, 64);
    }

    [Fact]
    public async Task Saving_and_deleting_a_document_tell_the_people_watching_it()
    {
        using var watching = _engine.Realtime.Subscribe("bob@example.com", "Order", "o1")!;
        using var elsewhere = _engine.Realtime.Subscribe("bob@example.com", "Order", "o2")!;

        await _engine.Documents.InsertAsync("Order", Values(("title", "o1")), Ann);
        await _engine.Documents.UpdateAsync("Order", "o1", Values(("note", "x")), Ann);
        await _engine.Documents.DeleteAsync("Order", "o1", Ann);

        var first = Next(watching)!;
        Assert.Equal(("doc", "ann@example.com", "Order", "o1", false), (first.Type, first.User, first.DocType, first.Name, first.Deleted));
        Assert.False(Next(watching)!.Deleted);
        Assert.True(Next(watching)!.Deleted);
        Assert.Null(Next(elsewhere));
    }

    [Fact]
    public async Task A_new_notification_is_announced_to_its_recipient_only()
    {
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", "bob@example.com"), ("full_name", "Bob"), ("enabled", true)), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", "bob@example.com"), ("role", "Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Order", Values(("title", "o1")), Ann);
        using var bob = _engine.Realtime.Subscribe("bob@example.com")!;
        using var ann = _engine.Realtime.Subscribe("ann@example.com")!;

        var written = await _engine.Notifications.NotifyAsync("bob@example.com", "Mention", "Order", "o1", "ann@example.com", "Ann mentioned you");

        Assert.True(written);
        Assert.Equal("notification", Next(bob)!.Type);
        Assert.Null(Next(ann));
    }
}
