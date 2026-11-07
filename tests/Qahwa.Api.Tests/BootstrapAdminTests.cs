using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Qahwa.Core.Auth;
using Xunit;

namespace Qahwa.Api.Tests;

/// <summary>The first administrator made from settings (ADR-162).</summary>
public sealed class BootstrapAdminTests
{
    private static IConfiguration Settings(string? email, string? password) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Qahwa:Bootstrap:AdminEmail"] = email,
        ["Qahwa:Bootstrap:AdminPassword"] = password,
    }).Build();

    [Fact]
    public async Task Makes_the_administrator_once_and_never_resets_the_password()
    {
        using var factory = new QahwaFactory(realAuth: true);
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

        await SiteStartup.BootstrapAdminAsync(scope.ServiceProvider, Settings("boot@example.com", "First-Password-123"), NullLogger.Instance);
        Assert.True(await auth.UserExistsAsync("boot@example.com"));

        // A second start with another password must leave the account as it is.
        await SiteStartup.BootstrapAdminAsync(scope.ServiceProvider, Settings("boot@example.com", "Other-Password-456"), NullLogger.Instance);
        Assert.True(await auth.CheckPasswordAsync("boot@example.com", "First-Password-123"));
        Assert.False(await auth.CheckPasswordAsync("boot@example.com", "Other-Password-456"));
    }

    [Fact]
    public async Task Does_nothing_without_settings_and_survives_a_weak_password()
    {
        using var factory = new QahwaFactory(realAuth: true);
        using var scope = factory.Services.CreateScope();
        var auth = scope.ServiceProvider.GetRequiredService<AuthService>();

        await SiteStartup.BootstrapAdminAsync(scope.ServiceProvider, Settings(null, null), NullLogger.Instance);
        await SiteStartup.BootstrapAdminAsync(scope.ServiceProvider, Settings("weak@example.com", "short"), NullLogger.Instance);
        Assert.False(await auth.UserExistsAsync("weak@example.com"));
    }
}
