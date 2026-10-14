using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

/// <summary>The real server with an in-memory repository and test DocTypes, so no database is needed.</summary>
public sealed class QahwaFactory(string environment = "Development", bool devAuth = true) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qahwa:AutoMigrate"] = "false",
            ["Qahwa:Api:DevAuthEnabled"] = devAuth.ToString(),
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMetaRegistry>();
            services.AddSingleton<IMetaRegistry>(TestMetas.Registry());
            services.RemoveAll<IDocumentRepository>();
            services.AddSingleton<IDocumentRepository>(new InMemoryDocumentRepository());
        });
    }

    public HttpClient ClientFor(string? user, string? roles = null)
    {
        var client = CreateClient();
        if (user is not null)
            client.DefaultRequestHeaders.Add("X-Qahwa-User", user);
        if (roles is not null)
            client.DefaultRequestHeaders.Add("X-Qahwa-Roles", roles);
        return client;
    }
}

public class HealthTests
{
    [Fact]
    public async Task Health_endpoint_returns_ok()
    {
        await using var factory = new QahwaFactory();

        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
