using Qahwa.Core;

namespace Qahwa.Data.Tests;

public class PostgresRealtimeBackplaneTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Events_pass_between_two_servers_through_the_database_in_order()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        await db.MigrateAsync();
        var hubA = new RealtimeHub();
        var hubB = new RealtimeHub();
        var a = new RealtimeBackplane(db.Registry, db.Repository, hubA, db.Clock);
        var b = new RealtimeBackplane(db.Registry, db.Repository, hubB, db.Clock);
        using var onB = hubB.Subscribe("ann@example.com", "Order", "o'1")!;
        await b.PollAsync();

        for (var i = 0; i < 11; i++)
            a.Publish(new RealtimeEvent("doc", $"u{i}@example.com", "Order", "o'1"));
        await a.FlushAsync();
        var delivered = await b.PollAsync();

        Assert.Equal(11, delivered);
        var users = new List<string>();
        while (onB.Reader.TryRead(out var m))
            users.Add(m.User);
        Assert.Equal(Enumerable.Range(0, 11).Select(i => $"u{i}@example.com"), users);
        Assert.Equal(0, await b.PollAsync());
    }
}
