using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class SignUpApiTests : IAsyncDisposable
{
    private const string Good = "a-long-passphrase-9";

    private readonly QahwaFactory _factory = new(realAuth: true, signUp: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<string> TokenAsync(int mails = 1)
    {
        for (var i = 0; i < 100 && _factory.Mail.Sent.Count < mails; i++)
            await Task.Delay(50);
        var body = _factory.Mail.Sent[mails - 1].Body;
        var start = body.IndexOf("token=", StringComparison.Ordinal) + 6;
        return Uri.UnescapeDataString(body[start..body.IndexOf('\n', start)].Trim());
    }

    [Fact]
    public async Task The_page_can_ask_whether_sign_up_is_on()
    {
        var on = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/auth/signup");
        await using var off = new QahwaFactory(realAuth: true);
        var no = await off.CreateClient().GetFromJsonAsync<JsonElement>("/api/auth/signup");

        Assert.True(on.GetProperty("data").GetProperty("enabled").GetBoolean());
        Assert.False(no.GetProperty("data").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task The_whole_journey_mail_link_password_sign_in()
    {
        var client = _factory.CreateClient();

        var asked = await client.PostAsJsonAsync("/api/auth/signup", new { email = "new@example.com", full_name = "New Person" });
        Assert.Equal(HttpStatusCode.OK, asked.StatusCode);
        var token = await TokenAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = "new@example.com", password = Good })).StatusCode);

        var confirmed = await client.PostAsJsonAsync("/api/auth/signup/confirm", new { token, password = Good });
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { email = "new@example.com", password = Good })).StatusCode);
        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("new@example.com", me.GetProperty("data").GetProperty("user").GetString());
        Assert.DoesNotContain("System Manager", me.GetRawText());

        var again = await _factory.CreateClient().PostAsJsonAsync("/api/auth/signup/confirm", new { token, password = "another-long-passphrase-7" });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
    }

    [Fact]
    public async Task The_answer_is_the_same_for_a_new_and_an_existing_address()
    {
        await _factory.Auth.EnsureUserAsync("ann@example.com", "Ann", Good, ["Clerk"]);
        var client = _factory.CreateClient();

        var fresh = await client.PostAsJsonAsync("/api/auth/signup", new { email = "new@example.com", full_name = "New" });
        var existing = await client.PostAsJsonAsync("/api/auth/signup", new { email = "ann@example.com", full_name = "Impostor" });

        Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
        Assert.Equal(await fresh.Content.ReadAsStringAsync(), await existing.Content.ReadAsStringAsync());
        await TokenAsync(1);
        for (var i = 0; i < 100 && _factory.Mail.Sent.Count < 2; i++)
            await Task.Delay(50);
        Assert.Equal("You already have an account", _factory.Mail.Sent.Single(m => m.To == "ann@example.com").Subject);
    }

    [Fact]
    public async Task Bad_input_is_a_clear_error_and_sends_nothing()
    {
        var client = _factory.CreateClient();

        foreach (var body in new object[]
                 {
                     new { email = "nope", full_name = "X" }, new { email = "a@example.com", full_name = "" }, new { email = "a@example.com", full_name = "<script>alert(1)</script>" },
                     new { email = "a@example.com", full_name = "see https://evil.example" }, new { },
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/signup", body)).StatusCode);
        await Task.Delay(200);
        Assert.Empty(_factory.Mail.Sent);
    }

    [Fact]
    public async Task A_weak_password_is_refused_and_the_link_still_works_afterwards()
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/auth/signup", new { email = "new@example.com", full_name = "New" });
        var token = await TokenAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/signup/confirm", new { token, password = "password1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/signup/confirm", new { token = "forged", password = Good })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/signup/confirm", new { token, password = Good })).StatusCode);
    }

    [Fact]
    public async Task When_it_is_off_both_doors_are_closed()
    {
        await using var off = new QahwaFactory(realAuth: true);
        var client = off.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/auth/signup", new { email = "a@example.com", full_name = "A" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/signup/confirm", new { token = "x", password = Good })).StatusCode);
        Assert.Empty(off.Mail.Sent);
    }

    [Fact]
    public async Task Asking_over_and_over_is_slowed_down_without_telling_anyone_anything()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/signup", new { email = "same@example.com", full_name = "Same" })).StatusCode);
        await Task.Delay(500);

        Assert.Equal(3, _factory.Mail.Sent.Count); // only three links an hour for one address
    }
}
