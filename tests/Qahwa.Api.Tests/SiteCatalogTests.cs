using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Qahwa.Hosting;

namespace Qahwa.Api.Tests;

public sealed class SiteCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qahwa-sites-" + Guid.NewGuid().ToString("N"));

    public SiteCatalogTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Site(string name, string json)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, SiteCatalog.ConfigFile);
        File.WriteAllText(file, json);
        return file;
    }

    private static string Json(string[] hosts, string db) => $$"""{ "hosts": [{{string.Join(",", hosts.Select(h => $"\"{h}\""))}}], "connectionString": "Host=db;Database={{db}}" }""";

    [Fact]
    public void Sites_are_read_from_their_folders_in_a_stable_order()
    {
        Site("globex", Json(["globex.example.com"], "globex"));
        Site("acme", Json(["Acme.Example.com:8080", "*.acme.example.com"], "acme"));
        Directory.CreateDirectory(Path.Combine(_root, "not-a-site")); // no site_config.json: ignored

        var sites = SiteCatalog.Load(_root);

        Assert.Equal(["acme", "globex"], sites.Select(s => s.Name));
        Assert.Equal(["acme.example.com", "*.acme.example.com"], sites[0].Hosts);
        Assert.Equal("Host=db;Database=acme", sites[0].ConnectionString);
    }

    [Fact]
    public void A_missing_directory_means_no_sites()
    {
        Assert.Empty(SiteCatalog.Load(Path.Combine(_root, "nowhere")));
    }

    [Fact]
    public void The_connection_string_can_come_from_an_environment_variable()
    {
        Site("acme", """{ "hosts": ["acme.test"], "connectionStringEnv": "ACME_DB" }""");

        var sites = SiteCatalog.Load(_root, name => name == "ACME_DB" ? "Host=secret;Database=acme" : null);
        Assert.Equal("Host=secret;Database=acme", sites.Single().ConnectionString);

        var missing = Assert.Throws<SiteConfigException>(() => SiteCatalog.Load(_root, _ => null));
        Assert.Contains("ACME_DB", missing.Message);
    }

    [Fact]
    public void Every_mistake_is_reported_at_once_and_stops_the_server()
    {
        Site("Bad_Name", Json(["a.test"], "a"));
        Site("nohosts", """{ "connectionString": "Host=db;Database=n" }""");
        Site("nodb", """{ "hosts": ["nodb.test"] }""");
        Site("one", Json(["shared.test", "bad host!"], "same"));
        Site("two", Json(["shared.test"], "SAME"));
        Site("broken", "{ not json");

        var error = Assert.Throws<SiteConfigException>(() => SiteCatalog.Load(_root));

        Assert.Contains(error.Problems, p => p.Contains("Bad_Name"));
        Assert.Contains(error.Problems, p => p.Contains("'nohosts' has no hosts"));
        Assert.Contains(error.Problems, p => p.Contains("nodb") && p.Contains("connectionString"));
        Assert.Contains(error.Problems, p => p.Contains("'shared.test' belongs to more than one site"));
        Assert.Contains(error.Problems, p => p.Contains("share one database"));
        Assert.Contains(error.Problems, p => p.Contains("bad host!"));
        Assert.Contains(error.Problems, p => p.Contains("broken"));
    }

    [Theory]
    [InlineData("Acme.Example.COM", "acme.example.com")]
    [InlineData("acme.example.com:8080", "acme.example.com")]
    [InlineData("acme.example.com.", "acme.example.com")]
    [InlineData("localhost", "localhost")]
    [InlineData("[::1]:5101", "[::1]")]
    [InlineData("127.0.0.1:80", "127.0.0.1")]
    public void Host_names_are_normalised(string raw, string expected) => Assert.Equal(expected, SiteCatalog.NormalizeHost(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a b.test")]
    [InlineData("acme.test/evil")]
    [InlineData("acme.test@evil.test")]
    [InlineData("-acme.test")]
    [InlineData("acme..test")]
    [InlineData("acme.test:80:80")]
    [InlineData("acme.test:port")]
    [InlineData("*.acme.test")] // wildcards are for the configuration only
    [InlineData("acme.test\r\nX-Evil: 1")]
    [InlineData("[not-an-address]")]
    public void Odd_host_names_match_nothing(string? raw) => Assert.Null(SiteCatalog.NormalizeHost(raw));

    [Fact]
    public void A_wildcard_is_for_the_configuration_and_needs_a_domain_behind_it()
    {
        Assert.Equal("*.acme.test", SiteCatalog.NormalizeHost("*.ACME.test", allowWildcard: true));
        Assert.Null(SiteCatalog.NormalizeHost("*.test", allowWildcard: true));
        Assert.Null(SiteCatalog.NormalizeHost("a.*.test", allowWildcard: true));
    }

    [Fact]
    public void The_site_is_found_by_exact_name_or_one_label_wildcard_and_nothing_else_matches()
    {
        var acme = new SiteDefinition("acme", ["acme.test", "*.shop.acme.test"], "db1", "x");
        var globex = new SiteDefinition("globex", ["globex.test", "special.shop.acme.test"], "db2", "x");
        var resolver = new SiteResolver([acme, globex]);

        Assert.Same(acme, resolver.Resolve("ACME.test:5101"));
        Assert.Same(acme, resolver.Resolve("eu.shop.acme.test"));
        Assert.Same(globex, resolver.Resolve("special.shop.acme.test")); // an exact name beats a wildcard
        Assert.Null(resolver.Resolve("a.b.shop.acme.test")); // a wildcard covers one label
        Assert.Null(resolver.Resolve("shop.acme.test"));
        Assert.Null(resolver.Resolve("acme.test.evil.com"));
        Assert.Null(resolver.Resolve("evilacme.test"));
        Assert.Null(resolver.Resolve("acme.test@evil.com"));
        Assert.Null(resolver.Resolve("*.shop.acme.test")); // a request cannot name a wildcard
        Assert.Null(resolver.Resolve(null));
        Assert.Null(resolver.Resolve(""));
        Assert.False(resolver.IsEmpty);
        Assert.True(new SiteResolver([]).IsEmpty);
    }

    [Fact]
    public void A_site_gets_its_own_database_folders_and_links_and_cannot_change_its_database_from_its_file()
    {
        var file = Site("acme", """{ "hosts": ["acme.test"], "connectionString": "Host=db;Database=acme", "ConnectionStrings": { "Default": "Host=main;Database=main" }, "Qahwa": { "Site": { "Name": "main" } } }""");
        var shared = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Host=main;Database=main",
            ["Qahwa:Files:Path"] = "/srv/files",
            ["Qahwa:Backup:Path"] = "/srv/backups",
            ["Qahwa:Auth:PublicUrl"] = "https://main.example.com",
        }).Build();
        var site = new SiteDefinition("acme", ["acme.test"], "Host=db;Database=acme", file);

        var config = SiteHost.SiteConfiguration(site, shared, new FakeEnvironment());

        Assert.Equal("Host=db;Database=acme", config.GetConnectionString("Default"));
        Assert.Equal("acme", config["Qahwa:Site:Name"]);
        Assert.Equal(Path.Combine("/srv/files", "sites", "acme"), config["Qahwa:Files:Path"]);
        Assert.Equal(Path.Combine("/srv/backups", "sites", "acme"), config["Qahwa:Backup:Path"]);
        Assert.Equal("https://acme.test", config["Qahwa:Auth:PublicUrl"]);
    }

    [Fact]
    public void A_site_without_a_plain_host_name_gets_no_link_address_unless_it_says_so()
    {
        var file = Site("wild", """{ "hosts": ["*.wild.test"], "connectionString": "x" }""");
        var shared = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Qahwa:Auth:PublicUrl"] = "https://main.example.com" }).Build();

        var config = SiteHost.SiteConfiguration(new SiteDefinition("wild", ["*.wild.test"], "x", file), shared, new FakeEnvironment());

        Assert.Null(config["Qahwa:Auth:PublicUrl"]); // never the main site's address
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";

        public string ApplicationName { get; set; } = "t";

        public string ContentRootPath { get; set; } = "/app";

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
