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
        roles.Add(UserContext.AllRole); // like a real session: everyone signed in has it

        return ValueTask.FromResult<IUserContext>(new UserContext(user, roles));
    }
}

/// <summary>
/// The real server with an in-memory repository and test DocTypes, so no database is needed.
/// By default requests are authenticated with test headers; pass <c>realAuth</c> to use the real cookie login.
/// </summary>
/// <summary>An app the test server has installed: one method that greets, one that needs a role.</summary>
public sealed class FactoryTestApp : IQahwaApp
{
    public string Name => "testapp";

    public string Version => "0.1.0";

    public void Configure(IAppBuilder app)
    {
        app.Method("hello", ctx => Task.FromResult<object?>(new { greeting = "Hello " + (ctx.Text("who") ?? "world"), user = ctx.User.User }), new MethodOptions { AllowGet = true });
        app.Method("secret", _ => Task.FromResult<object?>("classified"), new MethodOptions { Roles = ["System Manager"] });
    }
}

public sealed class QahwaFactory(
    string environment = "Development",
    bool realAuth = false,
    int loginPermitsPerMinute = 1000,
    bool signUp = false,
    bool ldap = false,
    string? sitesPath = null,
    bool mainSite = true) : WebApplicationFactory<Program>
{
    /// <summary>Every email the server tried to send.</summary>
    public FakeEmailSender Mail { get; } = new();

    /// <summary>The PDF maker the server uses in tests: it keeps the pages it was asked to print.</summary>
    public FakePdfRenderer Pdf { get; } = new();

    /// <summary>The stand-in identity provider behind the "idp" single sign-on provider.</summary>
    public FakeIdentityProvider IdentityProvider { get; } = new();

    /// <summary>The stand-in SAML provider behind the "saml" single sign-on provider.</summary>
    public FakeSamlIdp SamlIdp { get; } = new();

    /// <summary>The stand-in company directory (used when the factory is made with <c>ldap: true</c>).</summary>
    public FakeLdapDirectory Directory { get; } = new();

    /// <summary>What each extra site (see <c>sitesPath</c>) tried to send by email, by site name.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, FakeEmailSender> SiteMail { get; } = new();

    private sealed class TestSites(QahwaFactory factory) : ISiteServicesCustomizer
    {
        public void Customize(string siteName, IServiceCollection services) => factory.UseTestStores(services, factory.SiteMail.GetOrAdd(siteName, _ => new FakeEmailSender()));
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        // Read by Program before the host is built, so they must be host settings (ConfigureAppConfiguration comes too late).
        if (sitesPath is not null)
            builder.UseSetting("Qahwa:Sites:Path", sitesPath);
        if (!mainSite)
            builder.UseSetting("ConnectionStrings:Default", "");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Qahwa:AutoMigrate"] = "false",
            ["Qahwa:Scheduler:Enabled"] = "false",
            ["Qahwa:Auth:HashIterations"] = "1000",
            ["Qahwa:Auth:SessionCacheSeconds"] = "0",
            ["Qahwa:Auth:LoginPermitsPerMinute"] = loginPermitsPerMinute.ToString(),
            ["Qahwa:Auth:SignUp:Enabled"] = signUp ? "true" : "false",
            ["Qahwa:Auth:SignUp:DefaultRoles:0"] = "Clerk",
            ["Qahwa:Auth:Saml:Providers:saml:DisplayName"] = "Company SAML",
            ["Qahwa:Auth:Saml:Providers:saml:IdpEntityId"] = FakeSamlIdp.EntityId,
            ["Qahwa:Auth:Saml:Providers:saml:SsoUrl"] = FakeSamlIdp.SsoUrl,
            ["Qahwa:Auth:Saml:Providers:saml:Certificates:0"] = SamlIdp.CertificateText,
            ["Qahwa:Auth:Ldap:Enabled"] = ldap ? "true" : "false",
            ["Qahwa:Auth:Ldap:Server"] = "ldap.example.com",
            ["Qahwa:Auth:Ldap:BaseDn"] = "dc=example,dc=com",
            ["Qahwa:Auth:Ldap:AutoCreate"] = "true",
            ["Qahwa:Auth:Ldap:AllowedDomains:0"] = "example.com",
            ["Qahwa:Auth:Ldap:DefaultRoles:0"] = "Clerk",
            ["Qahwa:Auth:Sso:Providers:idp:DisplayName"] = "Company login",
            ["Qahwa:Auth:Sso:Providers:idp:Authority"] = FakeIdentityProvider.Authority,
            ["Qahwa:Auth:Sso:Providers:idp:ClientId"] = FakeIdentityProvider.ClientId,
            ["Qahwa:Auth:Sso:Providers:idp:ClientSecret"] = "s3cret",
        }));
        builder.ConfigureServices(services =>
        {
            UseTestStores(services, Mail);
            services.AddSingleton<IQahwaApp>(new FactoryTestApp());
            services.AddSingleton<ISiteServicesCustomizer>(new TestSites(this));
        });
    }

    private void UseTestStores(IServiceCollection services, FakeEmailSender mail)
    {
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
            services.RemoveAll<IPdfRenderer>();
            services.AddSingleton<IPdfRenderer>(Pdf);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(mail);
            services.RemoveAll<ISessionStore>();
            services.AddSingleton<ISessionStore>(new InMemorySessionStore());
            services.RemoveAll<ITotpStore>();
            services.AddSingleton<ITotpStore>(new InMemoryTotpStore());
            services.RemoveAll<ISecretProtector>();
            services.AddSingleton<ISecretProtector>(new FakeSecretProtector());
            services.RemoveAll<IOidcBackchannel>();
            services.AddSingleton<IOidcBackchannel>(IdentityProvider);
            services.RemoveAll<ILdapDirectory>();
            services.AddSingleton<ILdapDirectory>(Directory);
            services.RemoveAll<IIdentityStore>();
            services.AddSingleton<IIdentityStore>(new InMemoryIdentityStore());
            services.RemoveAll<IAuthStore>();
            services.AddSingleton<IAuthStore>(new InMemoryAuthStore());

            if (!realAuth)
            {
                services.RemoveAll<IUserContextResolver>();
                services.AddSingleton<IUserContextResolver, HeaderUserContextResolver>();
            }
        }
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
