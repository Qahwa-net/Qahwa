using Qahwa.Core.Auth;

namespace Qahwa.Data.Tests;

public class PostgresTwoFactorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime Created = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [SkippableFact]
    public async Task A_step_and_a_recovery_code_can_each_be_used_only_once_even_at_the_same_time()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        await db.TotpStore.SaveAsync(new TotpRecord("a@example.com", "secret", true, 10, ["h1", "h2", "h3"], Created));

        var steps = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.TotpStore.TryAdvanceStepAsync("a@example.com", 11)));
        Assert.Equal(1, steps.Count(s => s));
        Assert.False(await db.TotpStore.TryAdvanceStepAsync("a@example.com", 11));
        Assert.False(await db.TotpStore.TryAdvanceStepAsync("a@example.com", 5));

        var used = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => db.TotpStore.TryConsumeRecoveryAsync("a@example.com", "h2")));
        Assert.Equal(1, used.Count(s => s));
        var record = (await db.TotpStore.GetAsync("a@example.com"))!;
        Assert.Equal(["h1", "h3"], record.RecoveryHashes);
        Assert.Equal(11, record.LastStep);
        Assert.Equal(Created, record.UserCreated);
    }

    [SkippableFact]
    public async Task Records_are_replaced_deleted_and_a_pending_one_cannot_be_used_to_sign_in()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        await db.TotpStore.SaveAsync(new TotpRecord("b@example.com", "one", false, 0, [], Created));
        await db.TotpStore.SaveAsync(new TotpRecord("b@example.com", "two", false, 0, [], Created));

        Assert.Equal("two", (await db.TotpStore.GetAsync("b@example.com"))!.Secret);
        Assert.False(await db.TotpStore.TryAdvanceStepAsync("b@example.com", 1)); // not enabled
        Assert.False(await db.TotpStore.TryConsumeRecoveryAsync("b@example.com", "x"));
        await db.TotpStore.DeleteAsync("b@example.com");
        Assert.Null(await db.TotpStore.GetAsync("b@example.com"));
    }
}
