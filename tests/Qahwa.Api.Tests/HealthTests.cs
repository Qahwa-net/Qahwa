using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

/// <summary>Trusts X-Qahwa-User / X-Qahwa-Roles headers. Test-only: it does not exist in the product.</summary>
internal sealed class HeaderUserContextResolver : IUserContextResolver
{
    public ValueTask<IUserContext> ResolveAsync(HttpContext context, CancellationToken ct = default)
    {
        var user = context.Request.Headers["X-Qahwa-User"].ToString();
        if (string.IsNullOrWhiteSpace(user))
            return ValueTask.FromResult<IUserContext>(UserContext.Anonymous);

        var roles = context.Request.Headers["X-Qahwa-Roles"].ToString()
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        if (user == UserContext.AdministratorRole)
            roles.Add(UserContext.AdministratorRole);

        return ValueTask.FromResult<IUserContext>(new UserContext(user, roles));
    }
}

/// <summary>
/// The real server with an in-memory repository and test DocTypes, so no database is needed.
/// By default requests are authenticated with test headers; pass <c>realAuth</c> to use the real cookie login.
/// </summary>
public sealed class QahwaFactory(
    string environment = "Development",
    bool realAuth = false,
    int loginPermitsPerMinute = 1000) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qahwa:AutoMigrate"] = "false",
            ["Qahwa:Scheduler:Enabled"] = "false",
            ["Qahwa:Auth:HashIterations"] = "1000",
            ["Qahwa:Auth:SessionCacheSeconds"] = "0",
            ["Qahwa:Auth:LoginPermitsPerMinute"] = loginPermitsPerMinute.ToString(),
        }));
        builder.ConfigureServices(services =>
        {
            var registry = TestMetas.Registry();
            services.RemoveAll<IMetaRegistry>();
            services.RemoveAll<IMutableMetaRegistry>();
            services.RemoveAll<MetaRegistry>();
            services.AddSingleton(registry);
            services.AddSingleton<IMetaRegistry>(registry);
            services.AddSingleton<IMutableMetaRegistry>(registry);
            services.RemoveAll<IDocTypeStore>();
            services.AddSingleton<IDocTypeStore>(new InMemoryDocTypeStore());
            services.RemoveAll<IFileStorage>();
            services.AddSingleton<IFileStorage>(new InMemoryFileStorage());
            services.RemoveAll<IWebhookStore>();
            services.AddSingleton<IWebhookStore>(new InMemoryWebhookStore());
            services.RemoveAll<INotificationRuleStore>();
            services.AddSingleton<INotificationRuleStore>(new InMemoryNotificationRuleStore());
            services.RemoveAll<IAssignmentRuleStore>();
            services.AddSingleton<IAssignmentRuleStore>(new InMemoryAssignmentRuleStore());
            services.RemoveAll<IWorkflowStore>();
            services.AddSingleton<IWorkflowStore>(new InMemoryWorkflowStore());
            services.RemoveAll<ICustomizationStore>();
            services.AddSingleton<ICustomizationStore>(new InMemoryCustomizationStore());
            services.RemoveAll<IPermissionStore>();
            services.AddSingleton<IPermissionStore>(new InMemoryPermissionStore());
            services.RemoveAll<ISchemaManager>();
            services.AddSingleton<ISchemaManager>(new InMemorySchemaManager());
            services.RemoveAll<IDocumentRepository>();
            services.AddSingleton<IDocumentRepository>(new InMemoryDocumentRepository());
            services.RemoveAll<ISessionStore>();
            services.AddSingleton<ISessionStore>(new InMemorySessionStore());
            services.RemoveAll<ITotpStore>();
            services.AddSingleton<ITotpStore>(new InMemoryTotpStore());
            services.RemoveAll<ISecretProtector>();
            services.AddSingleton<ISecretProtector>(new FakeSecretProtector());
            services.RemoveAll<IAuthStore>();
            services.AddSingleton<IAuthStore>(new InMemoryAuthStore());

            if (!realAuth)
            {
                services.RemoveAll<IUserContextResolver>();
                services.AddSingleton<IUserContextResolver, HeaderUserContextResolver>();
            }
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

    public AuthService Auth => Services.GetRequiredService<AuthService>();
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

    [Fact]
    public async Task Responses_carry_security_headers_and_api_responses_are_not_cached()
    {
        await using var factory = new QahwaFactory();

        var response = await factory.CreateClient().GetAsync("/api/health");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Unknown_api_paths_return_the_json_envelope()
    {
        await using var factory = new QahwaFactory();

        var response = await factory.CreateClient().GetAsync("/api/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("not_found", await response.Content.ReadAsStringAsync());
    }
}
