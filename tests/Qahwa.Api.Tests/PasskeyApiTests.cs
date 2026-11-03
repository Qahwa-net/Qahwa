using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

/// <summary>Passkeys end to end: a stand-in device makes and uses them against the real endpoints (only storage is in memory).</summary>
public sealed class PasskeyApiTests : IAsyncDisposable
{
    private const string Email = "ann@example.com";
    private const string Password = TestUsers.StrongPassword;
    private const string Origin = "http://localhost";
    private const string Rp = "localhost";

    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> DataOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").Clone();

    private async Task<HttpClient> SignedInAsync()
    {
        await _factory.Auth.EnsureUserAsync(Email, "Ann", Password, ["Administrator"]);
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password })).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<(string Token, string Challenge)> BeginAsync(HttpClient client, string url, object? body = null)
    {
        var data = await DataOf(await client.PostAsJsonAsync(url, body ?? new { }));
        return (data.GetProperty("token").GetString()!, data.GetProperty("publicKey").GetProperty("challenge").GetString()!);
    }

    private static async Task<HttpResponseMessage> RegisterAsync(HttpClient client, FakeAuthenticator device, string rp = Rp, string origin = Origin, string label = "Laptop")
    {
        var (token, challenge) = await BeginAsync(client, "/api/auth/passkeys/begin", new { password = Password });
        var made = device.Create(challenge, rp, origin);
        return await client.PostAsJsonAsync("/api/auth/passkeys", new { token, id = made.Id, clientDataJson = made.ClientDataJson, attestationObject = made.AttestationObject, label });
    }

    private static async Task<HttpResponseMessage> SignInAsync(HttpClient client, FakeAuthenticator device, Action<Dictionary<string, object?>>? change = null,
        bool verified = true, bool tamper = false, string origin = Origin, string rp = Rp, string? challengeOverride = null, string? userHandle = null)
    {
        var (token, challenge) = await BeginAsync(client, "/api/auth/passkey/login/begin");
        var got = device.Get(challengeOverride ?? challenge, rp, origin, verified, tamper, userHandle: userHandle);
        var body = new Dictionary<string, object?>
        {
            ["token"] = token, ["id"] = got.Id, ["clientDataJson"] = got.ClientDataJson, ["authenticatorData"] = got.AuthenticatorData, ["signature"] = got.Signature, ["userHandle"] = got.UserHandle,
        };
        change?.Invoke(body);
        return await client.PostAsJsonAsync("/api/auth/passkey/login", body);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_person_makes_a_passkey_and_signs_in_with_it_alone(bool rsa)
    {
        var device = new FakeAuthenticator(rsa);
        var made = await RegisterAsync(await SignedInAsync(), device);
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var fresh = _factory.CreateClient();
        var login = await SignInAsync(fresh, device);

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(Email, (await DataOf(login)).GetProperty("user").GetString());
        Assert.Equal(Email, (await DataOf(await fresh.GetAsync("/api/auth/me"))).GetProperty("user").GetString()); // the cookie is a real session
        var list = await DataOf(await (await SignedInAsync()).GetAsync("/api/auth/passkeys"));
        Assert.Equal("Laptop", list[0].GetProperty("label").GetString());
        Assert.NotEqual(JsonValueKind.Null, list[0].GetProperty("lastUsed").ValueKind);
    }

    [Fact]
    public async Task Making_a_passkey_needs_a_session_and_the_password_again()
    {
        await SignedInAsync();
        var anonymous = _factory.CreateClient();
        var signedIn = await SignedInAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/auth/passkeys/begin", new { password = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedIn.PostAsJsonAsync("/api/auth/passkeys/begin", new { password = "wrong-password-123" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/auth/passkeys")).StatusCode);
    }

    [Fact]
    public async Task A_passkey_made_for_another_domain_or_page_is_refused()
    {
        var client = await SignedInAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(client, new FakeAuthenticator(), rp: "evil.example")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RegisterAsync(client, new FakeAuthenticator(), origin: "http://evil.example")).StatusCode);
        Assert.Empty((await DataOf(await client.GetAsync("/api/auth/passkeys"))).EnumerateArray());
    }

    [Fact]
    public async Task A_sign_in_answer_is_worth_nothing_when_anything_is_off()
    {
        var device = new FakeAuthenticator();
        await RegisterAsync(await SignedInAsync(), device);
        var fresh = () => _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, tamper: true)).StatusCode);                        // bad signature
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, verified: false)).StatusCode);                     // person not checked
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, origin: "http://evil.example")).StatusCode);       // wrong page
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, rp: "evil.example")).StatusCode);                  // wrong domain
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, challengeOverride: "AAAA")).StatusCode);           // not the challenge asked
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, userHandle: "AAAA")).StatusCode);                  // someone else's handle
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), new FakeAuthenticator())).StatusCode);                     // unknown passkey
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(fresh(), device, b => b["token"] = "garbage")).StatusCode);         // not our challenge
        Assert.Equal(HttpStatusCode.OK, (await SignInAsync(fresh(), device)).StatusCode);                                                // and the real thing still works
    }

    [Fact]
    public async Task An_answer_cannot_be_replayed_and_a_counter_that_goes_back_is_refused()
    {
        var device = new FakeAuthenticator();
        await RegisterAsync(await SignedInAsync(), device);
        var client = _factory.CreateClient();
        var (token, challenge) = await BeginAsync(client, "/api/auth/passkey/login/begin");
        var got = device.Get(challenge, Rp, Origin);
        var body = new { token, id = got.Id, clientDataJson = got.ClientDataJson, authenticatorData = got.AuthenticatorData, signature = got.Signature, userHandle = got.UserHandle };

        Assert.Equal(HttpStatusCode.OK, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/passkey/login", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/auth/passkey/login", body)).StatusCode); // same bytes again

        device.Counter = 0; // a clone that starts again from the beginning
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(_factory.CreateClient(), device)).StatusCode);
    }

    [Fact]
    public async Task A_passkey_can_be_made_once_removed_by_its_owner_only_and_stops_working()
    {
        var device = new FakeAuthenticator();
        var client = await SignedInAsync();
        await RegisterAsync(client, device);
        Assert.Equal(HttpStatusCode.Conflict, (await RegisterAsync(client, device)).StatusCode); // the same device twice
        var id = (await DataOf(await client.GetAsync("/api/auth/passkeys")))[0].GetProperty("id").GetString();

        await _factory.Auth.EnsureUserAsync("bob@example.com", "Bob", Password, ["Administrator"]);
        var bob = _factory.CreateClient();
        await bob.PostAsJsonAsync("/api/auth/login", new { email = "bob@example.com", password = Password });
        Assert.Equal(HttpStatusCode.NotFound, (await bob.DeleteAsync($"/api/auth/passkeys/{id}")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/auth/passkeys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(_factory.CreateClient(), device)).StatusCode);
    }

    [Fact]
    public async Task A_disabled_account_cannot_sign_in_with_its_passkey()
    {
        var device = new FakeAuthenticator();
        await RegisterAsync(await SignedInAsync(), device);
        var repository = _factory.Services.GetRequiredService<IDocumentRepository>();
        await repository.UpdateAsync(_factory.Services.GetRequiredService<IMetaRegistry>().Get("User"), Email, new Dictionary<string, object?> { ["enabled"] = false }, null, null);

        Assert.Equal(HttpStatusCode.Unauthorized, (await SignInAsync(_factory.CreateClient(), device)).StatusCode);
    }
}
