using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class PasswordResetApiTests : IAsyncDisposable
{
    private const string Email = "ann@example.com";
    private const string Old = TestUsers.StrongPassword;
    private const string New = "a-brand-new-passphrase-9";

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<string> LinkAsync()
    {
        var sent = await WaitForMailAsync(1);
        var body = sent[0].Body;
        var start = body.IndexOf("http", StringComparison.Ordinal);
        return body[start..body.IndexOf('\n', start)].Trim();
    }

    private async Task<IReadOnlyList<Qahwa.Core.EmailMessage>> WaitForMailAsync(int count)
    {
        for (var i = 0; i < 100; i++)
        {
            if (_factory.Mail.Sent.Count >= count)
                break;
            await Task.Delay(50);
        }

        return _factory.Mail.Sent;
    }

    [Fact]
    public async Task The_answer_is_the_same_for_a_real_and_an_unknown_address()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", Old, ["Clerk"]);
        var client = _factory.CreateClient();

        var real = await client.PostAsJsonAsync("/api/auth/forgot", new { email = Email });
        var unknown = await client.PostAsJsonAsync("/api/auth/forgot", new { email = "nobody@example.com" });
        var junk = await client.PostAsJsonAsync("/api/auth/forgot", new { email = "x\r\nBcc: a@b.com" });
        var none = await client.PostAsJsonAsync("/api/auth/forgot", new { });

        Assert.All(new[] { real, unknown, junk, none }, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(await real.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal(await real.Content.ReadAsStringAsync(), await junk.Content.ReadAsStringAsync());
        await WaitForMailAsync(1);
        await Task.Delay(200);
        Assert.Single(_factory.Mail.Sent); // only the real account got mail
        Assert.Equal(Email, _factory.Mail.Sent[0].To);
    }

    [Fact]
    public async Task The_link_in_the_email_resets_the_password_end_to_end()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", Old, ["Clerk"]);
        var signedIn = _factory.CreateClient();
        await signedIn.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old });
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/forgot", new { email = Email });
        var link = await LinkAsync();
        var token = Uri.UnescapeDataString(link[(link.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        Assert.StartsWith("http://localhost/reset-password?token=", link);

        var weak = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, new_password = "password1234" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal("new_password", (await weak.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("details")[0].GetProperty("field").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/reset-password", new { token, new_password = New })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.GetAsync("/api/auth/me")).StatusCode); // the old session ended
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = New })).StatusCode);

        var again = await _factory.CreateClient().PostAsJsonAsync("/api/auth/reset-password", new { token, new_password = "yet-another-passphrase-3" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("invalid_reset_link", (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Bad_tokens_are_refused_the_same_way()
    {
        var client = _factory.CreateClient();

        foreach (var token in new[] { "", "nope", new string('a', 3000) })
        {
            var response = await client.PostAsJsonAsync("/api/auth/reset-password", new { token, new_password = New });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("invalid_reset_link", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task In_production_the_link_never_uses_the_request_address_and_nothing_is_sent_without_a_configured_site()
    {
        await using var production = new QahwaFactory(environment: "Production", realAuth: true);
        await production.Auth.EnsureUserAsync(Email, "Ann", Old, ["Clerk"]);
        var client = production.CreateClient();
        client.DefaultRequestHeaders.Host = "evil.example.com"; // a forged Host header

        var response = await client.PostAsJsonAsync("/api/auth/forgot", new { email = Email });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await Task.Delay(300);
        Assert.Empty(production.Mail.Sent); // no PublicUrl: no link was made from the Host header
    }

    [Fact]
    public async Task Weak_passwords_are_refused_when_changing_the_password_too()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", Old, ["Clerk"]);
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old });

        var weak = await client.PostAsJsonAsync("/api/auth/change-password", new { old_password = Old, new_password = "1234567890" });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
    }
}
