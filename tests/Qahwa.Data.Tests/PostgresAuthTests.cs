using Qahwa.Core;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Data.Tests;

/// <summary>Login, lockout and credential rules against real PostgreSQL (needs Docker).</summary>
public class PostgresAuthTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Email = "alice@example.com";
    private const string Password = TestUsers.StrongPassword;

    private async Task<PgStack> StackAsync()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        return await postgres.CreateStackAsync();
    }

    [SkippableFact]
    public async Task Create_login_and_load_session_roundtrip()
    {
        await using var db = await StackAsync();
        await db.Auth.EnsureUserAsync("Alice@Example.com", "Alice", Password, ["Sales Manager", "System Manager"]);

        var session = await db.Auth.LoginAsync(Email, Password);

        Assert.NotNull(session);
        Assert.Equal(["All", "Sales Manager", "System Manager"], session.User.Roles.Order());
        var reloaded = await db.Auth.LoadSessionAsync(Email);
        Assert.Equal(session.PasswordVersion, reloaded!.PasswordVersion); // timestamps survive the database exactly
    }

    [SkippableFact]
    public async Task Ensure_user_is_idempotent()
    {
        await using var db = await StackAsync();

        await db.Auth.EnsureUserAsync(Email, "Alice", Password, ["System Manager"]);
        await db.Auth.EnsureUserAsync(Email, "Alice", Password, ["System Manager"]);

        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabUser\""));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"tabHas Role\""));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"__auth\""));
    }

    [SkippableFact]
    public async Task Password_is_stored_only_as_a_hash_outside_the_doctype_tables()
    {
        await using var db = await StackAsync();
        await db.Auth.EnsureUserAsync(Email, "Alice", Password, []);

        var credential = await db.AuthStore.GetAsync(Email);

        Assert.NotNull(credential);
        Assert.DoesNotContain(Password, credential.PasswordHash);
        Assert.StartsWith("AQAAAA", credential.PasswordHash); // Identity v3 format marker
        var user = await db.Documents.GetAsync("User", Email, TestUsers.Admin);
        Assert.DoesNotContain("password", user.Keys);
    }

    [SkippableFact]
    public async Task Concurrent_failures_are_all_counted_and_lock_the_account()
    {
        await using var db = await StackAsync();
        await db.Auth.EnsureUserAsync(Email, "Alice", Password, []);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => db.Auth.LoginAsync(Email, "wrong password " + i)));

        var credential = await db.AuthStore.GetAsync(Email);
        Assert.True(credential!.FailedAttempts >= db.AuthOptions.MaxFailedAttempts);
        Assert.NotNull(credential.LockedUntil);
        Assert.Null(await db.Auth.LoginAsync(Email, Password));
    }

    [SkippableFact]
    public async Task Lock_expiry_and_success_reset_work_in_sql()
    {
        await using var db = await StackAsync();
        await db.Auth.EnsureUserAsync(Email, "Alice", Password, []);
        for (var i = 0; i < db.AuthOptions.MaxFailedAttempts; i++)
            await db.Auth.LoginAsync(Email, "wrong password " + i);
        Assert.Null(await db.Auth.LoginAsync(Email, Password));

        db.Clock.Advance(TimeSpan.FromMinutes(db.AuthOptions.LockoutMinutes + 1));
        Assert.Null(await db.Auth.LoginAsync(Email, "wrong after expiry"));
        var afterOne = await db.AuthStore.GetAsync(Email);
        Assert.Equal(1, afterOne!.FailedAttempts);
        Assert.Null(afterOne.LockedUntil);

        Assert.NotNull(await db.Auth.LoginAsync(Email, Password));
        Assert.Equal(0, (await db.AuthStore.GetAsync(Email))!.FailedAttempts);
    }

    [SkippableFact]
    public async Task Concurrent_first_time_password_sets_do_not_fail()
    {
        await using var db = await StackAsync();
        await db.Documents.InsertAsync("User", Values(("email", Email), ("full_name", "Alice")), TestUsers.Admin);

        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => db.Auth.SetPasswordAsync(Email, Password)));

        Assert.NotNull(await db.Auth.LoginAsync(Email, Password));
        Assert.Equal(1, await db.ScalarAsync("SELECT COUNT(*) FROM \"__auth\""));
    }

    [SkippableFact]
    public async Task Rehash_only_applies_if_the_stored_hash_is_unchanged()
    {
        await using var db = await StackAsync();
        await db.Auth.EnsureUserAsync(Email, "Alice", Password, []);
        var original = (await db.AuthStore.GetAsync(Email))!.PasswordHash;

        await db.AuthStore.RehashAsync(Email, "some other hash", "attacker hash");
        Assert.Equal(original, (await db.AuthStore.GetAsync(Email))!.PasswordHash);

        await db.AuthStore.RehashAsync(Email, original, "upgraded hash");
        Assert.Equal("upgraded hash", (await db.AuthStore.GetAsync(Email))!.PasswordHash);
    }

    [SkippableFact]
    public async Task Recreated_user_does_not_inherit_old_credentials_or_roles()
    {
        await using var db = await StackAsync();
        await db.Auth.EnsureUserAsync(Email, "Alice", Password, ["Sales Manager"]);
        await db.Documents.DeleteAsync("User", Email, TestUsers.Admin);
        db.Clock.Advance(TimeSpan.FromSeconds(1));

        await db.Documents.InsertAsync("User", Values(("email", Email), ("full_name", "New Alice")), TestUsers.Admin);

        Assert.Null(await db.Auth.LoginAsync(Email, Password));
        await db.Auth.SetPasswordAsync(Email, "a brand new password");
        var session = await db.Auth.LoginAsync(Email, "a brand new password");
        Assert.Equal([UserContext.AllRole], session!.User.Roles);
    }

    [SkippableFact]
    public async Task Core_doctype_tables_exist_after_migration_and_migration_is_idempotent()
    {
        await using var db = await StackAsync();

        foreach (var name in new[] { "User", "Role", "Has Role" })
            Assert.True((await db.Sync.PlanAsync(db.Registry.Get(name))).IsEmpty, name);
    }
}
