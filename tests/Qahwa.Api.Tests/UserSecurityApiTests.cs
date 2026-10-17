using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class UserSecurityApiTests : IAsyncDisposable
{
    private readonly QahwaFactory factory = new();
    private HttpClient Admin => factory.ClientFor("Administrator");
    public ValueTask DisposeAsync() => factory.DisposeAsync();
    private const string Email = "staff@example.com";
    private const string NewPassword = "Blue-clouds-over-valleys-87";

    private async Task ConfigureEmailAsync()
    {
        var response = await Admin.PutAsJsonAsync("/api/resource/Email%20Account/Email%20Account", new
        {
            enabled = true, email_address = "desk@example.com", smtp_server = "smtp.example.com", smtp_port = 587,
            use_tls = true, smtp_username = "desk@example.com", smtp_password = "smtp-secret-421", site_url = "https://desk.example.com",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Password_changes_require_manager_enforce_policy_and_end_sessions()
    {
        await factory.Auth.EnsureUserAsync(Email, "Staff", TestUsers.StrongPassword, ["Employee"]);
        var sessions = factory.Services.GetRequiredService<SessionService>();
        var session = await sessions.StartAsync(Email, null, "test");
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync($"/api/users/{Email}/password", new { newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(Email, "Employee").PostAsJsonAsync($"/api/users/{Email}/password", new { newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync($"/api/users/{Email}/password", new { newPassword = "short" })).StatusCode);
        Assert.True(await sessions.IsValidAsync(session, Email));
        Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsJsonAsync($"/api/users/{Email}/password", new { newPassword = NewPassword })).StatusCode);
        Assert.True(await factory.Auth.CheckPasswordAsync(Email, NewPassword));
        Assert.False(await sessions.IsValidAsync(session, Email));
    }

    [Fact]
    public async Task Managers_cannot_change_an_administrator_even_when_disabled()
    {
        await factory.Auth.EnsureUserAsync(Email, "Staff", TestUsers.StrongPassword, ["Administrator"]);
        await Admin.PutAsJsonAsync($"/api/resource/User/{Email}", new { enabled = false });
        var manager = factory.ClientFor("manager@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/users/{Email}/password", new { newPassword = NewPassword })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/users/{Email}/reset-password", new { })).StatusCode);
    }

    [Fact]
    public async Task Saved_email_secrets_are_masked_and_configuration_is_live()
    {
        await ConfigureEmailAsync();
        var json = await Admin.GetStringAsync("/api/resource/Email%20Account/Email%20Account");
        Assert.DoesNotContain("smtp-secret-421", json);
        Assert.Contains(DocumentService.PasswordMask, json);
        var service = factory.Services.GetRequiredService<EmailAccountService>();
        Assert.Equal("smtp-secret-421", (await service.GetAsync()).Password);
        Assert.Equal("smtp.example.com", (await service.GetAsync()).Host);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(Email, "Employee").GetAsync("/api/resource/Email%20Account/Email%20Account")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.ClientFor(Email, "Employee").PostAsJsonAsync("/api/email/test", new { to = Email })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsJsonAsync("/api/email/test", new { to = Email })).StatusCode);
        Assert.Single(factory.Mail.Sent);
        await Admin.PutAsJsonAsync("/api/resource/Email%20Account/Email%20Account", new { enabled = false });
        Assert.False(await service.IsConfiguredAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Admin.PostAsJsonAsync("/api/email/test", new { to = Email })).StatusCode);
    }

    [Fact]
    public async Task Reset_email_uses_saved_site_url_and_can_initialize_a_new_user_once()
    {
        await ConfigureEmailAsync();
        var created = await Admin.PostAsJsonAsync("/api/resource/User", new { email = Email, full_name = "New Staff", enabled = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.PostAsJsonAsync($"/api/users/{Email}/reset-password", new { })).StatusCode);
        var mail = Assert.Single(factory.Mail.Sent);
        Assert.Contains("https://desk.example.com/reset-password?token=", mail.Body);
        var link = mail.Body.Split('\n').First(line => line.StartsWith("https://", StringComparison.Ordinal));
        var token = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        var reset = factory.Services.GetRequiredService<PasswordResetService>();
        await reset.ResetAsync(token, NewPassword);
        Assert.True(await factory.Auth.CheckPasswordAsync(Email, NewPassword));
        Assert.Null(await reset.RedeemAsync(token));
    }
}
