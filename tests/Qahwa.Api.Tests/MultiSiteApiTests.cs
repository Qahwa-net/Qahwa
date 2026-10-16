using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class MultiSiteApiTests : IAsyncDisposable
{
    private const string Password = TestUsers.StrongPassword;

    private readonly string _sites = Path.Combine(Path.GetTempPath(), "qahwa-multisite-" + Guid.NewGuid().ToString("N"));
    private QahwaFactory? _factory;

    public ValueTask DisposeAsync()
    {
        var disposing = _factory?.DisposeAsync() ?? ValueTask.CompletedTask;
        if (Directory.Exists(_sites))
            Directory.Delete(_sites, recursive: true);
        return disposing;
    }

    private static void WriteSite(string root, string name, string hosts, string extra = "")
    {
        Directory.CreateDirectory(Path.Combine(root, name));
        File.WriteAllText(Path.Combine(root, name, "site_config.json"), $$"""{ "hosts": [{{hosts}}], "connectionString": "Host=unused;Database={{name}}" {{extra}} }""");
    }

    private QahwaFactory Start(bool realAuth = false, bool mainSite = true)
    {
        WriteSite(_sites, "acme", "\"acme.test\", \"*.acme.test\"", """, "Qahwa": { "Auth": { "SignUp": { "Enabled": true } } }""");
        WriteSite(_sites, "globex", "\"globex.test\"");
        return _factory = new QahwaFactory(realAuth: realAuth, sitesPath: _sites, mainSite: mainSite);
    }

    private HttpClient At(string host, string? user = null)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Host = host;
        if (user is not null)
            client.DefaultRequestHeaders.Add("X-Qahwa-User", user);
        return client;
    }

    private AuthService AuthOf(string site) =>
        _factory!.Services.GetRequiredService<SiteHost>().Sites.Single(s => s.Name == site).Provider.GetRequiredService<AuthService>();

    private static async Task<string[]> RolesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/resource/Role?limit=100")).GetProperty("data").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task Each_site_has_its_own_data()
    {
        Start();

        var made = await At("acme.test", "Administrator").PostAsJsonAsync("/api/resource/Role", new { role_name = "Acme Auditors" });

        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Contains("Acme Auditors", await RolesAsync(At("acme.test", "Administrator")));
        Assert.DoesNotContain("Acme Auditors", await RolesAsync(At("globex.test", "Administrator")));
        Assert.DoesNotContain("Acme Auditors", await RolesAsync(At("localhost", "Administrator"))); // the main site too
    }

    [Fact]
    public async Task A_one_label_wildcard_and_a_port_reach_the_same_site()
    {
        Start();
        await At("acme.test", "Administrator").PostAsJsonAsync("/api/resource/Role", new { role_name = "Wild" });

        Assert.Contains("Wild", await RolesAsync(At("shop.acme.test", "Administrator")));
        Assert.Contains("Wild", await RolesAsync(At("ACME.test:5101", "Administrator")));
        Assert.DoesNotContain("Wild", await RolesAsync(At("a.b.acme.test", "Administrator"))); // two labels: the main site
    }

    [Fact]
    public async Task Accounts_belong_to_one_site_and_a_sign_in_does_not_work_on_another()
    {
        Start(realAuth: true);
        await AuthOf("acme").EnsureUserAsync("ann@example.com", "Ann", Password, ["System Manager"]);
        await AuthOf("globex").EnsureUserAsync("gus@example.com", "Gus", Password, ["System Manager"]);

        Assert.Equal(HttpStatusCode.OK, (await At("acme.test").PostAsJsonAsync("/api/auth/login", new { email = "ann@example.com", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await At("globex.test").PostAsJsonAsync("/api/auth/login", new { email = "ann@example.com", password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await At("globex.test").PostAsJsonAsync("/api/auth/login", new { email = "gus@example.com", password = Password })).StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_from_one_site_is_worth_nothing_at_another_even_for_the_same_email()
    {
        Start(realAuth: true);
        await AuthOf("acme").EnsureUserAsync("ann@example.com", "Ann", Password, ["System Manager"]);
        await AuthOf("globex").EnsureUserAsync("ann@example.com", "Ann", Password, ["System Manager"]);
        var acme = At("acme.test");
        var login = await acme.PostAsJsonAsync("/api/auth/login", new { email = "ann@example.com", password = Password });
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("qahwa.sid=", StringComparison.Ordinal)).Split(';')[0];

        var stolen = At("globex.test");
        stolen.DefaultRequestHeaders.Add("Cookie", cookie);

        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await stolen.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Links_in_email_lead_to_the_site_that_sent_them_and_settings_stay_with_their_site()
    {
        Start(realAuth: true);
        var acme = At("acme.test");

        var asked = await acme.PostAsJsonAsync("/api/auth/signup", new { email = "new@example.com", full_name = "New Person" });
        var other = await At("globex.test").PostAsJsonAsync("/api/auth/signup", new { email = "new@example.com", full_name = "New Person" });

        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode); // sign-up is switched on for acme only
        for (var i = 0; i < 100 && _factory!.SiteMail.GetValueOrDefault("acme")?.Sent.Count is null or 0; i++)
            await Task.Delay(50);
        var mail = _factory!.SiteMail["acme"].Sent.Single();
        Assert.Contains("https://acme.test/sign-up/confirm?token=", mail.Body); // not the main site's address and not the request's
        Assert.Empty(_factory.Mail.Sent);
        Assert.Empty(_factory.SiteMail.GetValueOrDefault("globex")?.Sent ?? []);
    }

    [Fact]
    public async Task A_link_made_for_one_site_cannot_be_used_on_another()
    {
        Start();
        await At("acme.test").PostAsJsonAsync("/api/auth/signup", new { email = "new@example.com", full_name = "New Person" });
        for (var i = 0; i < 100 && _factory!.SiteMail.GetValueOrDefault("acme")?.Sent.Count is null or 0; i++)
            await Task.Delay(50);
        var body = _factory!.SiteMail["acme"].Sent.Single().Body;
        var start = body.IndexOf("token=", StringComparison.Ordinal) + 6;
        var token = Uri.UnescapeDataString(body[start..body.IndexOf('\n', start)].Trim());

        // The same token offered to the main site's sign-up (not enabled there) or to the other site: nothing.
        Assert.NotEqual(HttpStatusCode.OK, (await At("globex.test").PostAsJsonAsync("/api/auth/signup/confirm", new { token, password = Password })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await At("localhost").PostAsJsonAsync("/api/auth/signup/confirm", new { token, password = Password })).StatusCode);
    }

    [Fact]
    public async Task Without_a_main_site_unknown_hosts_are_turned_away_and_health_still_answers()
    {
        Start(mainSite: false);

        var unknown = await At("evil.test").GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.MisdirectedRequest, unknown.StatusCode);
        Assert.Equal("unknown_site", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await At("acme.test.evil.com", "Administrator").GetAsync("/api/resource/Role")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await At("evil.test").GetAsync("/api/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await At("globex.test", "Administrator").GetAsync("/api/resource/Role")).StatusCode);
    }

    [Fact]
    public async Task The_forwarded_host_header_cannot_choose_a_site()
    {
        Start(mainSite: false);
        var client = At("evil.test", "Administrator");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "acme.test");

        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await client.GetAsync("/api/resource/Role")).StatusCode);
    }

    [Fact]
    public void Two_sites_that_share_a_database_or_a_host_stop_the_server_from_starting()
    {
        WriteSite(_sites, "one", "\"same.test\"");
        WriteSite(_sites, "two", "\"same.test\"");

        _factory = new QahwaFactory(sitesPath: _sites);

        Assert.ThrowsAny<Exception>(() => _factory.CreateClient());
    }
}
