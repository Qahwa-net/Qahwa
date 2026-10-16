using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Qahwa.Api.Tests;

/// <summary>Sets the connection's address from a test header, because the in-memory server has none.</summary>
internal sealed class FakeAddressFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use((context, nextMiddleware) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip) && System.Net.IPAddress.TryParse(ip, out var address))
                context.Connection.RemoteIpAddress = address;
            return nextMiddleware(context);
        });
        next(app);
    };
}

public sealed class NetworkRestrictionTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();
    private readonly WebApplicationFactory<Program> _restricted;

    public NetworkRestrictionTests() =>
        _restricted = _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Qahwa:Auth:AllowedIps:0"] = "10.0.0.0/8",
                ["Qahwa:Auth:AllowedIps:1"] = "203.0.113.5",
            }));
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, FakeAddressFilter>());
        });

    public async ValueTask DisposeAsync()
    {
        await _restricted.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private HttpClient From(string? ip)
    {
        var client = _restricted.CreateClient();
        client.DefaultRequestHeaders.Add("X-Qahwa-User", "Administrator");
        client.DefaultRequestHeaders.Add("X-Qahwa-Roles", "Administrator");
        if (ip is not null)
            client.DefaultRequestHeaders.Add("X-Test-Ip", ip);
        return client;
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("203.0.113.5")]
    public async Task Allowed_networks_use_the_api_as_usual(string ip) =>
        Assert.Equal(HttpStatusCode.OK, (await From(ip).GetAsync("/api/meta")).StatusCode);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.6")]
    [InlineData(null)] // an address that cannot be read is never trusted
    public async Task Other_networks_get_403_before_anything_else_happens(string? ip)
    {
        var client = From(ip);

        var response = await client.GetAsync("/api/meta");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("permission_denied", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/auth/login", new { email = "a@b.com", password = "x" })).StatusCode);
    }

    [Fact]
    public async Task The_health_check_the_public_forms_and_the_desk_files_stay_open()
    {
        var outsider = From("8.8.8.8");

        Assert.Equal(HttpStatusCode.OK, (await outsider.GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/public/forms/nope")).StatusCode); // reached the form service, not blocked
        Assert.NotEqual(HttpStatusCode.Forbidden, (await outsider.GetAsync("/forms/nope")).StatusCode);
    }

    [Fact]
    public async Task A_bad_entry_in_the_configuration_stops_the_server()
    {
        await using var broken = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["Qahwa:Auth:AllowedIps:0"] = "10.0.0.0/99" })));

        var error = await Assert.ThrowsAnyAsync<Exception>(async () => await broken.CreateClient().GetAsync("/api/health"));

        Assert.Contains("AllowedIps", error.ToString());
    }
}

public sealed class SettingsApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Anyone_signed_in_reads_the_display_settings_and_a_manager_changes_them()
    {
        var clerk = _factory.ClientFor("c@example.com", "Clerk");
        var defaults = (await clerk.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("data");
        Assert.Equal("yyyy-mm-dd", defaults.GetProperty("dateFormat").GetString());
        Assert.Equal("Monday", defaults.GetProperty("firstDayOfWeek").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PutAsJsonAsync("/api/resource/System Settings/System Settings", new { date_format = "dd/mm/yyyy" })).StatusCode);
        var manager = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync("/api/resource/System Settings/System Settings", new { date_format = "dd/mm/yyyy", first_day_of_week = "Saturday", time_zone = "Asia/Riyadh" })).StatusCode);

        var changed = (await clerk.GetFromJsonAsync<JsonElement>("/api/settings")).GetProperty("data");
        Assert.Equal("dd/mm/yyyy", changed.GetProperty("dateFormat").GetString());
        Assert.Equal("Saturday", changed.GetProperty("firstDayOfWeek").GetString());
        Assert.Equal("Asia/Riyadh", changed.GetProperty("timeZone").GetString());
    }

    [Fact]
    public async Task Guests_are_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/settings")).StatusCode);
    }
}
