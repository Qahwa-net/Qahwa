using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Data.Tests;

public class PostgresApiKeyTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [SkippableFact]
    public async Task A_key_is_stored_hashed_and_authenticates_its_person_on_postgres()
    {
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        await using var db = await postgres.CreateStackAsync();
        await db.MigrateAsync();
        await db.Auth.EnsureUserAsync("keys@example.com", "Key Owner", TestUsers.StrongPassword, ["System Manager"]);
        var person = new UserContext("keys@example.com", ["System Manager"]);

        var key = await db.ApiKeys.CreateAsync(new ApiKeyRequest("integration", ReadOnly: true), person);

        var identity = await db.ApiKeys.AuthenticateAsync(key.Token);
        Assert.NotNull(identity);
        Assert.Equal(("keys@example.com", true), (identity.User.User, identity.ReadOnly));
        Assert.Null(await db.ApiKeys.AuthenticateAsync(key.Token + "x"));
        Assert.Single(await db.ApiKeys.ListAsync(person));

        await db.ApiKeys.RevokeAsync(key.Info.Id, person);
        Assert.Null(await db.ApiKeys.AuthenticateAsync(key.Token));
        Assert.Empty(await db.ApiKeys.ListAsync(person));
    }
}
