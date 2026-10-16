using Npgsql;
using Qahwa.Data.Postgres;

namespace Qahwa.Data.Tests;

public class PostgresSiteTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData("acme", "qahwa_acme")]
    [InlineData("acme-eu", "qahwa_acme_eu")]
    [InlineData("a1", "qahwa_a1")]
    public void The_database_is_named_after_the_site(string site, string expected) => Assert.Equal(expected, PostgresSites.DatabaseNameFor(site));

    [Theory]
    [InlineData("")]
    [InlineData("Acme")]
    [InlineData("a_b")]
    [InlineData("a b")]
    [InlineData("-a")]
    [InlineData("a\"; DROP DATABASE postgres; --")]
    public void Odd_site_names_are_refused(string site) => Assert.Throws<ArgumentException>(() => PostgresSites.DatabaseNameFor(site));

    [Theory]
    [InlineData("x\"; DROP DATABASE postgres; --")]
    [InlineData("Upper")]
    [InlineData("1starts_with_digit")]
    [InlineData("")]
    [InlineData("has space")]
    public async Task Hostile_database_names_never_reach_the_server(string name)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PostgresSites.CreateDatabaseAsync("Host=localhost;Username=x;Password=y", name));
        Assert.Throws<ArgumentException>(() => PostgresSites.ConnectionFor("Host=localhost", name));
    }

    [SkippableFact]
    public async Task A_site_database_is_made_once_and_is_a_database_of_its_own()
    {
        Skip.If(PostgresFixture.UseSqlServer, "This one is about PostgreSQL databases.");
        Skip.IfNot(postgres.Available, postgres.SkipReason);
        var admin = postgres.ConnectionString;
        var name = "qahwa_test_" + Guid.NewGuid().ToString("N")[..10];

        Assert.True(await PostgresSites.CreateDatabaseAsync(admin, name));
        Assert.False(await PostgresSites.CreateDatabaseAsync(admin, name)); // again: nothing happens

        await using var site = NpgsqlDataSource.Create(PostgresSites.ConnectionFor(admin, name));
        await using var command = site.CreateCommand("SELECT current_database()");
        Assert.Equal(name, await command.ExecuteScalarAsync());

        await using var cleanup = NpgsqlDataSource.Create(admin);
        await using var drop = cleanup.CreateCommand($"DROP DATABASE \"{name}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync();
    }
}
