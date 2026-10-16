using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class LdapApiTests : IAsyncDisposable
{
    private const string Dn = "uid=ann,ou=people,dc=example,dc=com";
    private const string Secret = "directory-pass-1";

    private readonly QahwaFactory _factory = new(realAuth: true, ldap: true);

    public LdapApiTests() => _factory.Directory.Add(Dn, "ann@example.com", Secret, "Ann Lee");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });

    [Fact]
    public async Task A_person_in_the_directory_signs_in_and_an_account_is_made_for_them()
    {
        var client = _factory.CreateClient();

        var response = await LoginAsync(client, "ann@example.com", Secret);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("ann@example.com", me.GetProperty("data").GetProperty("user").GetString());
        Assert.Contains("Clerk", me.GetRawText());
        Assert.DoesNotContain("System Manager", me.GetRawText());
    }

    [Fact]
    public async Task A_wrong_directory_password_is_the_same_failure_as_any_other()
    {
        var wrong = await LoginAsync(_factory.CreateClient(), "ann@example.com", "wrong-password-1");
        var unknown = await LoginAsync(_factory.CreateClient(), "nobody@example.com", "wrong-password-1");

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Local_accounts_keep_working_when_the_directory_has_nobody_by_that_name()
    {
        await _factory.Auth.EnsureUserAsync("bob@example.com", "Bob", "local-passphrase-9", ["Clerk"]);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_factory.CreateClient(), "bob@example.com", "local-passphrase-9")).StatusCode);
    }

    [Fact]
    public async Task A_directory_account_follows_the_directory_and_ignores_its_local_password()
    {
        await _factory.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", "local-passphrase-9", ["Clerk"]);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(_factory.CreateClient(), "ann@example.com", Secret)).StatusCode); // links her

        var local = await LoginAsync(_factory.CreateClient(), "ann@example.com", "local-passphrase-9");

        Assert.Equal(HttpStatusCode.Unauthorized, local.StatusCode);
    }

    [Fact]
    public async Task A_directory_outage_never_gives_a_session()
    {
        _factory.Directory.Down = true;
        var client = _factory.CreateClient();

        var response = await LoginAsync(client, "ann@example.com", Secret);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Search_tricks_in_the_email_do_not_sign_anyone_in()
    {
        foreach (var email in new[] { "*", "*)(mail=*", "ann@example.com)(|(mail=*" })
            Assert.NotEqual(HttpStatusCode.OK, (await LoginAsync(_factory.CreateClient(), email, Secret)).StatusCode);
        Assert.Empty(_factory.Directory.Binds);
    }

    [Fact]
    public async Task With_the_directory_off_nothing_changes()
    {
        await using var off = new QahwaFactory(realAuth: true);
        off.Directory.Add(Dn, "ann@example.com", Secret);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(off.CreateClient(), "ann@example.com", Secret)).StatusCode);
        Assert.Equal(0, off.Directory.Searches);
    }
}
