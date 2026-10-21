using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class SecurityPolicyTests
{
    private readonly TestEngine _engine = new();
    private const string Strong = "a-long-passphrase-9";

    private Task SaveAsync(params (string, object?)[] values) =>
        _engine.Documents.UpdateAsync("System Settings", "System Settings", Values(values), TestUsers.SystemManager);

    [Fact]
    public async Task Without_settings_the_configuration_applies()
    {
        _engine.AuthOptions.MinPasswordLength = 10;
        _engine.AuthOptions.AbsoluteSessionDays = 14;
        Assert.Equal(10, await _engine.SecurityPolicy.MinPasswordLengthAsync());
        Assert.Equal(14, await _engine.SecurityPolicy.AbsoluteSessionDaysAsync());
        Assert.Equal(0, await _engine.SecurityPolicy.PasswordMaxAgeDaysAsync());
    }

    [Fact]
    public async Task The_desk_can_only_make_the_rules_stricter_never_looser()
    {
        _engine.AuthOptions.MinPasswordLength = 10;
        _engine.AuthOptions.AbsoluteSessionDays = 14;
        _engine.AuthOptions.PasswordMaxAgeDays = 90;

        await SaveAsync(("min_password_length", 8), ("absolute_session_days", 30), ("password_max_age_days", 365));
        Assert.Equal(10, await _engine.SecurityPolicy.MinPasswordLengthAsync());   // 8 is looser than 10: ignored
        Assert.Equal(14, await _engine.SecurityPolicy.AbsoluteSessionDaysAsync()); // 30 is longer than 14: ignored
        Assert.Equal(90, await _engine.SecurityPolicy.PasswordMaxAgeDaysAsync());  // 365 is looser than 90: ignored

        await SaveAsync(("min_password_length", 16), ("absolute_session_days", 3), ("password_max_age_days", 30));
        Assert.Equal(16, await _engine.SecurityPolicy.MinPasswordLengthAsync());
        Assert.Equal(3, await _engine.SecurityPolicy.AbsoluteSessionDaysAsync());
        Assert.Equal(30, await _engine.SecurityPolicy.PasswordMaxAgeDaysAsync());
    }

    [Fact]
    public async Task A_password_expiry_can_be_added_where_the_configuration_has_none()
    {
        await SaveAsync(("password_max_age_days", 10));
        Assert.Equal(10, await _engine.SecurityPolicy.PasswordMaxAgeDaysAsync());
    }

    [Theory]
    [InlineData("min_password_length", 7)]
    [InlineData("min_password_length", 65)]
    [InlineData("absolute_session_days", 91)]
    [InlineData("absolute_session_days", -1)]
    [InlineData("password_max_age_days", 3651)]
    public async Task Out_of_range_values_are_refused_when_saved(string field, int value) =>
        await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync((field, value)));

    [Fact]
    public async Task A_longer_minimum_applies_to_new_passwords_and_a_shorter_session_to_new_sign_ins()
    {
        await SaveAsync(("min_password_length", 20), ("absolute_session_days", 2));
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Strong, []));
        Assert.Contains(ex.Errors, e => e.Message.Contains("20", StringComparison.Ordinal));
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "an-even-longer-passphrase-9", []);

        var id = await _engine.Sessions.StartAsync("ann@example.com", null, null);
        var session = (await _engine.Sessions.ListAsync("ann@example.com")).Single(s => s.Id == id);
        Assert.True(session.Expires <= _engine.Clock.GetUtcNow().UtcDateTime.AddDays(2).AddMinutes(1));
    }

    [Fact]
    public async Task Only_system_managers_can_change_them()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.UpdateAsync("System Settings", "System Settings", Values(("min_password_length", 30)), new UserContext("c@example.com", ["Clerk"])));
    }
}
