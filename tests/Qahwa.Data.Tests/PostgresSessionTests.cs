using Qahwa.Core.Auth;

namespace Qahwa.Data.Tests;

public class PostgresSessionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task Sessions_are_stored_listed_revoked_and_cleaned_up_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        var store = db.SessionStore;
        var now = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        await store.CreateAsync(new SessionRecord("s1", "a@example.com", now, now, now.AddDays(1), "1.1.1.1", "Chrome", false));
        await store.CreateAsync(new SessionRecord("s2", "a@example.com", now.AddMinutes(1), now, now.AddDays(1), null, null, false));
        await store.CreateAsync(new SessionRecord("s3", "b@example.com", now, now, now.AddDays(1), null, null, false));
        await store.CreateAsync(new SessionRecord("old", "a@example.com", now.AddDays(-5), now.AddDays(-5), now.AddDays(-4), null, null, false));

        Assert.Equal(["s2", "s1"], (await store.ListAsync("a@example.com", now)).Select(s => s.Id));
        Assert.False(await store.RevokeAsync("s3", "a@example.com")); // not hers
        Assert.True(await store.RevokeAsync("s1", "a@example.com"));
        Assert.Equal(["s2"], (await store.ListAsync("a@example.com", now)).Select(s => s.Id));
        Assert.Equal(2, await store.RevokeAllAsync("a@example.com", null)); // s2 and the expired one that was never revoked
        Assert.Single(await store.ListAsync("b@example.com", now));

        await store.TouchAsync("s3", now.AddHours(3));
        Assert.Equal(now.AddHours(3), (await store.GetAsync("s3"))!.LastSeen);
        await store.DeleteExpiredAsync(now);
        Assert.Null(await store.GetAsync("old"));
        Assert.NotNull(await store.GetAsync("s2"));
    }
}
