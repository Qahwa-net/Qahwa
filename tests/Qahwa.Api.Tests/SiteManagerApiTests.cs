using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Qahwa.Hosting;

namespace Qahwa.Api.Tests;

public sealed class SiteManagerApiTests : IAsyncDisposable
{
    private sealed class FakeDatabases : ISiteDatabases
    {
        public List<string> Made { get; } = [];

        public Task<(string Connection, bool Made)> CreateAsync(string provider, string adminConnection, string site, CancellationToken ct)
        {
            Made.Add(site);
            return Task.FromResult(($"Host=fake;Database=qahwa_{site}", true));
        }
    }

    private readonly string _sites = Path.Combine(Path.GetTempPath(), "qahwa-sitemanager-" + Guid.NewGuid().ToString("N"));
    private readonly FakeDatabases _databases = new();
    private readonly QahwaFactory _root;
    private readonly WebApplicationFactory<Program> _factory;

    public SiteManagerApiTests() : this(manage: true)
    {
    }

    private SiteManagerApiTests(bool manage)
    {
        Directory.CreateDirectory(_sites);
        File.WriteAllText(Path.Combine(_sites, "existing.txt"), "keep the folder");
        _root = new QahwaFactory(sitesPath: _sites);
        _factory = _root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Qahwa:Sites:Manage", manage ? "true" : "false");
            builder.UseSetting("Qahwa:Sites:MigrateNew", "false"); // the test stores have no schema to migrate
            builder.UseSetting("Qahwa:Sites:AdminConnection", "Host=fake;Username=admin");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISiteDatabases>();
                services.AddSingleton<ISiteDatabases>(_databases);
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _root.DisposeAsync();
        if (Directory.Exists(_sites))
            Directory.Delete(_sites, recursive: true);
    }

    private HttpClient At(string host, string? user = "Administrator", string? roles = "Administrator")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Host = host;
        if (user is not null)
            client.DefaultRequestHeaders.Add("X-Qahwa-User", user);
        if (roles is not null)
            client.DefaultRequestHeaders.Add("X-Qahwa-Roles", roles);
        return client;
    }

    private static object NewSite(string name = "acme", string host = "acme.test") =>
        new { name, hosts = new[] { host }, adminEmail = "ann@example.com", adminPassword = "A-long-passphrase-1!" };

    [Fact]
    public async Task A_new_site_answers_at_once_with_its_own_data_and_is_kept_for_the_next_start()
    {
        var made = await At("localhost").PostAsJsonAsync("/api/sites", NewSite());

        Assert.True(made.StatusCode == HttpStatusCode.Created, await made.Content.ReadAsStringAsync());
        Assert.Equal(["acme"], _databases.Made);
        Assert.Equal(HttpStatusCode.Created, (await At("acme.test").PostAsJsonAsync("/api/resource/Role", new { role_name = "Acme Auditors" })).StatusCode);
        var main = await At("localhost").GetFromJsonAsync<JsonElement>("/api/resource/Role?limit=100");
        Assert.DoesNotContain(main.GetProperty("data").EnumerateArray(), r => r.GetProperty("name").GetString() == "Acme Auditors");
        var stored = SiteCatalog.Load(_sites).Single();
        Assert.Equal("acme", stored.Name);
        Assert.Equal(["acme.test"], stored.Hosts);
        var list = await At("localhost").GetFromJsonAsync<JsonElement>("/api/sites");
        Assert.Equal("acme", list.GetProperty("data")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_site_that_clashes_with_a_running_one_is_refused_and_leaves_nothing_behind()
    {
        await At("localhost").PostAsJsonAsync("/api/sites", NewSite());

        var sameName = await At("localhost").PostAsJsonAsync("/api/sites", NewSite(host: "other.test"));
        var sameHost = await At("localhost").PostAsJsonAsync("/api/sites", NewSite("globex"));
        var badName = await At("localhost").PostAsJsonAsync("/api/sites", NewSite("Bad Name!", "bad.test"));
        var noAdmin = await At("localhost").PostAsJsonAsync("/api/sites", new { name = "nobody", hosts = new[] { "nobody.test" } });

        Assert.Equal(HttpStatusCode.Conflict, sameName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sameHost.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noAdmin.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_sites, "globex")));
        Assert.Equal(["acme"], SiteCatalog.Load(_sites).Select(s => s.Name));
    }

    [Fact]
    public async Task Removing_a_site_stops_it_answering_and_a_restart_does_not_bring_it_back()
    {
        await At("localhost").PostAsJsonAsync("/api/sites", NewSite());

        var removed = await At("localhost").DeleteAsync("/api/sites/acme");

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.Empty(SiteCatalog.Load(_sites));
        Assert.Single(Directory.GetFiles(Path.Combine(_sites, "acme"), "site_config.json.removed-*")); // kept, so it can be put back
        var list = await At("localhost").GetFromJsonAsync<JsonElement>("/api/sites");
        Assert.Equal(0, list.GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await At("localhost").DeleteAsync("/api/sites/acme")).StatusCode);
    }

    [Fact]
    public async Task Only_an_administrator_of_the_main_site_can_manage_sites()
    {
        await At("localhost").PostAsJsonAsync("/api/sites", NewSite());

        Assert.Equal(HttpStatusCode.Unauthorized, (await At("localhost", user: null, roles: null).GetAsync("/api/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await At("localhost", "clerk", "Clerk").GetAsync("/api/sites")).StatusCode);
        // The administrator of an extra site has nothing here: the endpoint does not exist for them.
        Assert.Equal(HttpStatusCode.NotFound, (await At("acme.test").GetAsync("/api/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await At("acme.test").DeleteAsync("/api/sites/acme")).StatusCode);
    }

    [Fact]
    public async Task Nothing_is_there_unless_the_operator_switched_it_on()
    {
        await using var off = new SiteManagerApiTests(manage: false);

        Assert.Equal(HttpStatusCode.NotFound, (await off.At("localhost").GetAsync("/api/sites")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await off.At("localhost").PostAsJsonAsync("/api/sites", NewSite())).StatusCode);
        Assert.Empty(off._databases.Made);
    }
}
