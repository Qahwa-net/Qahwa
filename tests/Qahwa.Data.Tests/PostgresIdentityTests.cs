namespace Qahwa.Data.Tests;

public class PostgresIdentityTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private const string Issuer = "https://idp.example.com";

    [SkippableFact]
    public async Task An_identity_belongs_to_one_user_and_cannot_be_taken_over_even_by_racing_requests()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => db.IdentityStore.LinkAsync(Issuer, "sub-1", $"user{i}@example.com", Now)));

        Assert.Equal(1, results.Count(r => r));
        var owner = await db.IdentityStore.FindUserAsync(Issuer, "sub-1");
        Assert.StartsWith("user", owner);
        Assert.Equal(["sub-1"], await db.IdentityStore.SubjectsOfAsync(Issuer, owner!));
        Assert.False(await db.IdentityStore.LinkAsync(Issuer, "sub-1", "intruder@example.com", Now));
        Assert.Equal(owner, await db.IdentityStore.FindUserAsync(Issuer, "sub-1"));
    }

    [SkippableFact]
    public async Task Lookups_are_exact_and_hostile_text_is_just_data()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        const string nasty = "x'); DROP TABLE \"__identities\"; --";
        Assert.True(await db.IdentityStore.LinkAsync(Issuer, nasty, "ann@example.com", Now));
        Assert.True(await db.IdentityStore.LinkAsync("https://other.example.com", "sub-1", "bob@example.com", Now)); // same subject, another issuer

        Assert.Equal("ann@example.com", await db.IdentityStore.FindUserAsync(Issuer, nasty));
        Assert.Null(await db.IdentityStore.FindUserAsync(Issuer, "SUB-1"));
        Assert.Null(await db.IdentityStore.FindUserAsync(Issuer, "%"));
        Assert.Equal("bob@example.com", await db.IdentityStore.FindUserAsync("https://other.example.com", "sub-1"));
        Assert.Empty(await db.IdentityStore.SubjectsOfAsync(Issuer, "nobody@example.com"));
    }
}
