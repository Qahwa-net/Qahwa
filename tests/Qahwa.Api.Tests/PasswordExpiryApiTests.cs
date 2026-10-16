using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class PasswordExpiryApiTests : IAsyncDisposable
{
    private const string Email = "ann@example.com";
    private const string Old = TestUsers.StrongPassword;
    private const string New = "a-brand-new-passphrase-9";

    private readonly QahwaFactory _factory = new(realAuth: true);
    private readonly WebApplicationFactory<Program> _expiring;

    public PasswordExpiryApiTests() =>
        _expiring = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Qahwa:Auth:PasswordMaxAgeDays"] = "30" })));

    public async ValueTask DisposeAsync()
    {
        await _expiring.DisposeAsync();
        await _factory.DisposeAsync();
    }

    private async Task AgePasswordAsync(int days)
    {
        await _expiring.Services.GetRequiredService<AuthService>().EnsureUserAsync(Email, "Ann", Old, ["Clerk"]);
        var store = _expiring.Services.GetRequiredService<IAuthStore>();
        var credential = (await store.GetAsync(Email))!;
        await store.SetPasswordAsync(Email, credential.PasswordHash, credential.UserCreated, DateTime.UtcNow.AddDays(-days));
    }

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_fresh_password_signs_in_as_usual()
    {
        await AgePasswordAsync(5);

        var login = await _expiring.CreateClient().PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), c => c.StartsWith("qahwa.sid="));
    }

    [Fact]
    public async Task An_expired_password_gets_no_session_only_a_way_to_choose_a_new_one()
    {
        await AgePasswordAsync(40);
        var client = _expiring.CreateClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.False(login.Headers.Contains("Set-Cookie"));
        var data = (await JsonOf(login)).GetProperty("data");
        Assert.True(data.GetProperty("password_expired").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        var token = data.GetProperty("token").GetString();

        var weak = await client.PostAsJsonAsync("/api/auth/expired-password", new { token, old_password = Old, new_password = "1234567890" });
        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        var changed = await client.PostAsJsonAsync("/api/auth/expired-password", new { token, old_password = Old, new_password = New });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old })).StatusCode);
        var again = await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = New });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains(again.Headers.GetValues("Set-Cookie"), c => c.StartsWith("qahwa.sid="));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/auth/expired-password", new { token, old_password = New, new_password = "yet-another-passphrase-3" })).StatusCode); // the token is spent
    }

    [Fact]
    public async Task Two_factor_still_applies_after_the_password_is_replaced_and_a_wrong_password_is_still_just_wrong()
    {
        await AgePasswordAsync(40);
        var store = _expiring.Services.GetRequiredService<ITotpStore>();
        var credential = (await _expiring.Services.GetRequiredService<IAuthStore>().GetAsync(Email))!;
        await store.SaveAsync(new TotpRecord(Email, "x", true, 0, [], credential.UserCreated));
        var client = _expiring.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = "wrong-password-123" })).StatusCode);
        var token = (await JsonOf(await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Old }))).GetProperty("data").GetProperty("token").GetString();
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/expired-password", new { token, old_password = Old, new_password = New })).StatusCode);

        var next = await JsonOf(await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = New }));
        Assert.True(next.GetProperty("data").GetProperty("two_factor").GetBoolean()); // the second step is still asked for
    }

    [Fact]
    public async Task Bad_tokens_are_refused()
    {
        foreach (var token in new[] { "", "nope", new string('a', 3000) })
        {
            var response = await _expiring.CreateClient().PostAsJsonAsync("/api/auth/expired-password", new { token, old_password = Old, new_password = New });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}
