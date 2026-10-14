using Qahwa.Core.Auth;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AuthServiceTests
{
    private const string Email = "alice@example.com";
    private const string Password = TestUsers.StrongPassword;

    private readonly TestEngine _engine = new();

    private Task CreateAliceAsync(params string[] roles) =>
        _engine.Auth.EnsureUserAsync(Email, "Alice", Password, roles);

    [Fact]
    public async Task Login_succeeds_with_roles_and_is_case_insensitive_on_email()
    {
        await CreateAliceAsync("Sales Manager", "System Manager");

        var session = await _engine.Auth.LoginAsync("  ALICE@Example.com ", Password);

        Assert.NotNull(session);
        Assert.Equal(Email, session.User.User);
        Assert.Equal("Alice", session.FullName);
        Assert.Equal(["Sales Manager", "System Manager"], session.User.Roles.Order());
    }

    [Theory]
    [InlineData("alice@example.com", "wrong password here")]
    [InlineData("nobody@example.com", "correct horse battery staple")]
    [InlineData("", "correct horse battery staple")]
    [InlineData("alice@example.com", "")]
    [InlineData(null, null)]
    public async Task Login_fails_the_same_way_for_every_kind_of_bad_credential(string? email, string? password)
    {
        await CreateAliceAsync();

        Assert.Null(await _engine.Auth.LoginAsync(email, password));
    }

    [Fact]
    public async Task Oversized_passwords_are_rejected_before_hashing()
    {
        await CreateAliceAsync();

        Assert.Null(await _engine.Auth.LoginAsync(Email, new string('x', 100_000)));
    }

    [Fact]
    public async Task Account_locks_after_repeated_failures_even_for_the_right_password()
    {
        await CreateAliceAsync();
        for (var i = 0; i < _engine.AuthOptions.MaxFailedAttempts; i++)
            Assert.Null(await _engine.Auth.LoginAsync(Email, "wrong password " + i));

        Assert.Null(await _engine.Auth.LoginAsync(Email, Password));
    }

    [Fact]
    public async Task Lock_expires_and_a_later_failure_starts_a_fresh_count()
    {
        await CreateAliceAsync();
        for (var i = 0; i < _engine.AuthOptions.MaxFailedAttempts; i++)
            await _engine.Auth.LoginAsync(Email, "wrong password " + i);

        _engine.Clock.Advance(TimeSpan.FromMinutes(_engine.AuthOptions.LockoutMinutes + 1));
        Assert.Null(await _engine.Auth.LoginAsync(Email, "still wrong password")); // fresh count of 1, not re-locked
        Assert.NotNull(await _engine.Auth.LoginAsync(Email, Password));
    }

    [Fact]
    public async Task Success_resets_the_failure_counter()
    {
        await CreateAliceAsync();
        for (var i = 0; i < _engine.AuthOptions.MaxFailedAttempts - 1; i++)
            await _engine.Auth.LoginAsync(Email, "wrong password " + i);

        Assert.NotNull(await _engine.Auth.LoginAsync(Email, Password));
        await _engine.Auth.LoginAsync(Email, "wrong again 1");

        Assert.NotNull(await _engine.Auth.LoginAsync(Email, Password));
    }

    [Fact]
    public async Task Disabled_users_cannot_log_in_and_lose_their_session()
    {
        await CreateAliceAsync();
        Assert.NotNull(await _engine.Auth.LoadSessionAsync(Email));

        await _engine.Documents.UpdateAsync("User", Email, Values(("enabled", false)), TestUsers.Admin);

        Assert.Null(await _engine.Auth.LoginAsync(Email, Password));
        Assert.Null(await _engine.Auth.LoadSessionAsync(Email));
    }

    [Fact]
    public async Task User_without_a_password_cannot_log_in()
    {
        await _engine.Documents.InsertAsync("User", Values(("email", Email), ("full_name", "Alice")), TestUsers.Admin);

        Assert.Null(await _engine.Auth.LoginAsync(Email, Password));
    }

    [Fact]
    public async Task A_recreated_user_does_not_inherit_the_old_password_or_roles()
    {
        await CreateAliceAsync("Sales Manager");
        await _engine.Documents.DeleteAsync("User", Email, TestUsers.Admin);
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));

        await _engine.Documents.InsertAsync("User", Values(("email", Email), ("full_name", "Alice 2")), TestUsers.Admin);

        Assert.Null(await _engine.Auth.LoginAsync(Email, Password)); // stale credential is ignored

        await _engine.Auth.SetPasswordAsync(Email, "a brand new password");
        var session = await _engine.Auth.LoginAsync(Email, "a brand new password");
        Assert.NotNull(session);
        Assert.Empty(session.User.Roles); // old Has Role rows predate this account
    }

    [Theory]
    [InlineData("short")]
    [InlineData("alice@example.com")]
    public async Task Password_policy_is_enforced(string password)
    {
        await CreateAliceAsync();

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Auth.SetPasswordAsync(Email, password));

        Assert.Equal("new_password", Assert.Single(ex.Errors).Field);
    }

    [Fact]
    public async Task Changing_the_password_requires_the_old_one_and_bumps_the_password_version()
    {
        await CreateAliceAsync();
        var before = (await _engine.Auth.LoginAsync(Email, Password))!;

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Auth.ChangePasswordAsync(Email, "not the password", "another long password"));

        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Auth.ChangePasswordAsync(Email, Password, "another long password");

        Assert.Null(await _engine.Auth.LoginAsync(Email, Password));
        var after = (await _engine.Auth.LoginAsync(Email, "another long password"))!;
        Assert.NotEqual(before.PasswordVersion, after.PasswordVersion);
    }

    [Fact]
    public async Task Email_is_stored_lowercase_and_ensure_user_is_idempotent()
    {
        await _engine.Auth.EnsureUserAsync("Alice@Example.COM", "Alice", Password, ["System Manager"]);
        await _engine.Auth.EnsureUserAsync("alice@example.com", "Alice", Password, ["System Manager"]);

        var users = await _engine.Documents.ListAsync("User", new ListRequest(), TestUsers.Admin);
        var roles = await _engine.Documents.ListAsync("Has Role", new ListRequest(), TestUsers.Admin);
        Assert.Equal(Email, Assert.Single(users.Data)["email"]);
        Assert.Single(roles.Data);
    }

    [Fact]
    public async Task Core_doctypes_are_only_visible_to_system_managers()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.ListAsync("User", new ListRequest(), TestUsers.Manager));
        Assert.Contains("User", _engine.Documents.ListMetas(new UserContext("sys", ["System Manager"])).Select(m => m.Name));
    }
}
