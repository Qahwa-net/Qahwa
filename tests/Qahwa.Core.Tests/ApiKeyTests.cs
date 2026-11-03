using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ApiKeyTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);

    private async Task PeopleAsync()
    {
        await _engine.Auth.EnsureUserAsync(Ann.User, "Ann", TestUsers.StrongPassword, ["Clerk"]);
        await _engine.Auth.EnsureUserAsync(Bob.User, "Bob", TestUsers.StrongPassword, ["Clerk"]);
    }

    private async Task<ApiKeyCreated> KeyAsync(UserContext user, ApiKeyRequest? request = null) =>
        await _engine.ApiKeys.CreateAsync(request ?? new ApiKeyRequest("Reporting script"), user);

    // ---- limiting a key to some DocTypes, and how many requests it may make ----

    private void DocTypes()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Deal Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Deal", "fields": [ { "name": "title", "type": "Data" }, { "name": "lines", "type": "Table", "options": "Deal Line" } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Contract", "fields": [ { "name": "title", "type": "Data" } ] }"""));
    }

    [Fact]
    public async Task A_key_can_be_limited_to_some_DocTypes_which_must_be_real_top_level_ones()
    {
        await PeopleAsync();
        DocTypes();

        var key = await KeyAsync(Ann, new ApiKeyRequest("Deals only", DocTypes: ["deal", " Contract ", "DEAL"]));

        Assert.Equal(["Deal", "Contract"], key.Info.DocTypes); // the registry's spelling, each once
        Assert.Equal(["Deal", "Contract"], (await _engine.ApiKeys.AuthenticateAsync(key.Token))!.DocTypes);
        Assert.Empty((await KeyAsync(Ann)).Info.DocTypes); // no limit by default

        async Task<DocumentValidationException> Fails(params string[] names) =>
            await Assert.ThrowsAsync<DocumentValidationException>(() => KeyAsync(Ann, new ApiKeyRequest("x", DocTypes: names)));
        Assert.Contains((await Fails("Nope")).Errors, e => e.Field == "docTypes");
        Assert.Contains((await Fails("Deal Line")).Errors, e => e.Field == "docTypes"); // a child table is reached through its parent
        Assert.Contains((await Fails(Enumerable.Range(0, 21).Select(i => $"D{i}").ToArray())).Errors, e => e.Field == "docTypes");
    }

    [Theory]
    [InlineData("/api/resource/Deal", true)]
    [InlineData("/api/resource/Deal/D-0001", true)]
    [InlineData("/api/resource/Deal/D-0001/comments", true)]
    [InlineData("/api/v1/resource/deal", true)]
    [InlineData("/api/resource/Contract", true)]
    [InlineData("/api/meta/Deal", true)]
    [InlineData("/api/resource/Deal%20Line", false)]
    [InlineData("/api/resource/User", false)]
    [InlineData("/api/resource/Dealer", false)]
    [InlineData("/api/meta/User", false)]
    [InlineData("/api/method/anything", false)]
    [InlineData("/api/share/Deal/D-1", false)]
    [InlineData("/api/api-keys", false)]
    [InlineData("/api/backup", false)]
    [InlineData("/api/resource", false)]
    [InlineData("/api/auth/me", false)]
    [InlineData("/resource/Deal", false)]
    public void A_limited_key_can_only_use_the_resource_and_metadata_of_its_DocTypes(string path, bool allowed) =>
        Assert.Equal(allowed, ApiKeyService.Allows(["Deal", "Contract"], path));

    [Fact]
    public void A_key_with_no_limit_can_use_every_address() => Assert.True(ApiKeyService.Allows([], "/api/anything/at/all"));

    [Fact]
    public async Task A_key_can_be_limited_in_requests_per_minute_and_each_key_counts_on_its_own()
    {
        await PeopleAsync();
        var slow = await KeyAsync(Ann, new ApiKeyRequest("Slow", RatePerMinute: 3));
        var other = await KeyAsync(Ann, new ApiKeyRequest("Other", RatePerMinute: 3));
        var free = await KeyAsync(Ann);

        for (var i = 0; i < 3; i++)
            Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(slow.Token));
        var ex = await Assert.ThrowsAsync<ApiKeyRateLimitedException>(() => _engine.ApiKeys.AuthenticateAsync(slow.Token));
        Assert.InRange(ex.RetryAfterSeconds, 1, 60);
        Assert.Equal(429, ex.StatusCode);

        Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(other.Token)); // another key has its own count
        for (var i = 0; i < 20; i++)
            Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(free.Token)); // no limit set

        _engine.Clock.Advance(TimeSpan.FromSeconds(61)); // a new minute
        Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(slow.Token));
    }

    [Fact]
    public async Task The_rate_must_be_a_sensible_number()
    {
        await PeopleAsync();
        foreach (var bad in new[] { 0, -5, ApiKeyService.MaxRate + 1 })
        {
            var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => KeyAsync(Ann, new ApiKeyRequest("x", RatePerMinute: bad)));
            Assert.Contains(ex.Errors, e => e.Field == "ratePerMinute");
        }

        Assert.Equal(60, (await KeyAsync(Ann, new ApiKeyRequest("x", RatePerMinute: 60))).Info.RatePerMinute);
        Assert.Null((await KeyAsync(Ann)).Info.RatePerMinute);
    }

    // ---- making and using keys ----

    [Fact]
    public async Task A_key_stands_for_its_person_with_their_current_roles()
    {
        await PeopleAsync();
        var key = await KeyAsync(Ann);

        Assert.StartsWith("qk_", key.Token);
        var identity = await _engine.ApiKeys.AuthenticateAsync(key.Token);

        Assert.NotNull(identity);
        Assert.Equal(("ann@example.com", false), (identity.User.User, identity.ReadOnly));
        Assert.Equal(["Clerk", UserContext.AllRole], identity.User.Roles);

        // Roles are read fresh: a role taken away is taken from the key too.
        var grant = (await _engine.Documents.ListAsync("Has Role", new ListRequest { Fields = ["name", "user"], Limit = 50 }, TestUsers.Admin)).Data.First(r => (string)r["user"]! == Ann.User);
        await _engine.Documents.DeleteAsync("Has Role", (string)grant["name"]!, TestUsers.Admin);
        Assert.Equal([UserContext.AllRole], (await _engine.ApiKeys.AuthenticateAsync(key.Token))!.User.Roles); // the lost role is gone; only the automatic one is left
    }

    [Fact]
    public async Task The_token_is_shown_once_and_only_a_hash_is_stored()
    {
        await PeopleAsync();
        var key = await KeyAsync(Ann);
        var secret = key.Token[(key.Token.IndexOf('.') + 1)..];

        var rows = (await _engine.Documents.ListAsync("API Key", new ListRequest { Fields = ["key_id", "description"], Limit = 10 }, TestUsers.SystemManager)).Data;
        await Assert.ThrowsAnyAsync<QahwaException>(() => _engine.Documents.ListAsync("API Key", new ListRequest { Fields = ["secret_hash"], Limit = 1 }, TestUsers.SystemManager)); // not even a System Manager can ask for it

        Assert.DoesNotContain(secret, string.Join('|', rows.SelectMany(r => r.Values).Select(v => v?.ToString())));
        Assert.Equal(key.Info.KeyId, rows.Single()["key_id"]);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(await _engine.ApiKeys.ListAsync(Ann)));
    }

    [Fact]
    public async Task Wrong_malformed_and_foreign_tokens_authenticate_nobody()
    {
        await PeopleAsync();
        var key = await KeyAsync(Ann);
        var other = await KeyAsync(Bob);
        var keyId = key.Info.KeyId;
        var secret = key.Token[(key.Token.IndexOf('.') + 1)..];

        foreach (var bad in new string?[]
        {
            null, "", "qk_", "qk_.", $"qk_{keyId}.", $"qk_{keyId}.{secret}x", $"qk_{keyId}.{secret[..^1]}", $"qk_{keyId.ToUpperInvariant()}.{secret}", $"qk_{keyId}{secret}",
            $"qk_{other.Info.KeyId}.{secret}", // someone else's id with this secret
            $"qk_{keyId}.{other.Token[(other.Token.IndexOf('.') + 1)..]}", $"Bearer qk_{keyId}.{secret}", $"qk_{new string('0', 12)}.{secret}",
            $"qk_{keyId}.{new string('A', 300)}", "' OR 1=1 --", $"qk_{keyId}' OR '1'='1.{secret}",
        })
            Assert.Null(await _engine.ApiKeys.AuthenticateAsync(bad));

        Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(key.Token));
    }

    [Fact]
    public async Task A_key_stops_working_when_it_expires_when_revoked_or_when_the_person_is_disabled()
    {
        await PeopleAsync();
        var expiring = await KeyAsync(Ann, new ApiKeyRequest("Temp", false, "2026-01-03"));
        var revoked = await KeyAsync(Ann);
        var disabled = await KeyAsync(Bob);
        Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(expiring.Token));

        _engine.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Null(await _engine.ApiKeys.AuthenticateAsync(expiring.Token));
        Assert.True((await _engine.ApiKeys.ListAsync(Ann)).Single(k => k.Id == expiring.Info.Id).Expired);

        await _engine.ApiKeys.RevokeAsync(revoked.Info.Id, Ann);
        Assert.Null(await _engine.ApiKeys.AuthenticateAsync(revoked.Token));

        var bob = (await _engine.Documents.ListAsync("User", new ListRequest { Fields = ["name", "email"], Limit = 20 }, TestUsers.Admin)).Data.First(r => (string)r["email"]! == Bob.User);
        await _engine.Documents.UpdateAsync("User", (string)bob["name"]!, Values(("enabled", false)), TestUsers.Admin);
        Assert.Null(await _engine.ApiKeys.AuthenticateAsync(disabled.Token));
    }

    [Fact]
    public async Task A_read_only_key_says_so_and_last_use_is_recorded_without_writing_every_time()
    {
        await PeopleAsync();
        var key = await KeyAsync(Ann, new ApiKeyRequest("Dashboard", ReadOnly: true));

        Assert.True((await _engine.ApiKeys.AuthenticateAsync(key.Token))!.ReadOnly);
        var first = (await _engine.ApiKeys.ListAsync(Ann)).Single().LastUsed;
        Assert.NotNull(first);

        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.ApiKeys.AuthenticateAsync(key.Token);
        Assert.Equal(first, (await _engine.ApiKeys.ListAsync(Ann)).Single().LastUsed); // not touched again within five minutes

        _engine.Clock.Advance(TimeSpan.FromMinutes(10));
        await _engine.ApiKeys.AuthenticateAsync(key.Token);
        Assert.True((await _engine.ApiKeys.ListAsync(Ann)).Single().LastUsed > first);
    }

    // ---- managing keys ----

    [Fact]
    public async Task People_see_and_revoke_only_their_own_keys_and_a_system_manager_all()
    {
        await PeopleAsync();
        var mine = await KeyAsync(Ann);
        var theirs = await KeyAsync(Bob);

        Assert.Equal([mine.Info.Id], (await _engine.ApiKeys.ListAsync(Ann, everyone: true)).Select(k => k.Id)); // asking for everyone's does nothing for an ordinary user
        Assert.Equal(2, (await _engine.ApiKeys.ListAsync(TestUsers.SystemManager, everyone: true)).Count);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ApiKeys.RevokeAsync(theirs.Info.Id, Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.ApiKeys.CreateAsync(new ApiKeyRequest(), UserContext.Anonymous));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.ApiKeys.ListAsync(UserContext.Anonymous));

        await _engine.ApiKeys.RevokeAsync(theirs.Info.Id, TestUsers.SystemManager);
        Assert.Null(await _engine.ApiKeys.AuthenticateAsync(theirs.Token));
        Assert.NotNull(await _engine.ApiKeys.AuthenticateAsync(mine.Token));
    }

    [Fact]
    public async Task Descriptions_expiry_dates_and_the_number_of_keys_are_checked()
    {
        await PeopleAsync();
        async Task<string> FieldOf(ApiKeyRequest request) =>
            (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ApiKeys.CreateAsync(request, Ann))).Errors.First().Field;

        Assert.Equal("description", await FieldOf(new(new string('d', 141))));
        Assert.Equal("description", await FieldOf(new("bad\u0001text")));
        Assert.Equal("expiresOn", await FieldOf(new("x", false, "soon")));
        Assert.Equal("expiresOn", await FieldOf(new("x", false, "2025-01-01"))); // in the past

        for (var i = 0; i < ApiKeyService.MaxPerUser; i++)
            await KeyAsync(Ann);
        Assert.Equal("description", await FieldOf(new("one more")));
        await KeyAsync(Bob); // somebody else's allowance is their own
    }

    [Fact]
    public async Task Every_key_is_different()
    {
        await PeopleAsync();
        var tokens = new List<string>();
        for (var i = 0; i < 5; i++)
            tokens.Add((await KeyAsync(Ann)).Token);

        Assert.Equal(5, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.True(t.Length > 50));
    }
}
