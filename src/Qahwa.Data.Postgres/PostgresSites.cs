using System.Text.RegularExpressions;
using Npgsql;

namespace Qahwa.Data.Postgres;

/// <summary>Creating the database of a new site (<c>qahwa new-site</c>).</summary>
public static partial class PostgresSites
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex SiteRule();

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$")]
    private static partial Regex DatabaseRule();

    /// <summary>The database for a site: <c>qahwa_</c> and the site name with dashes made underscores (<c>acme-eu</c> becomes <c>qahwa_acme_eu</c>).</summary>
    public static string DatabaseNameFor(string site) =>
        site is not null && SiteRule().IsMatch(site) ? "qahwa_" + site.Replace('-', '_') : throw new ArgumentException("A site name is lower-case letters, digits and dashes (up to 40).", nameof(site));

    /// <summary>The connection string for <paramref name="database"/> on the same server and with the same login as <paramref name="admin"/>.</summary>
    public static string ConnectionFor(string admin, string database)
    {
        if (!DatabaseRule().IsMatch(database))
            throw new ArgumentException("Not a usable database name.", nameof(database));
        return new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString;
    }

    /// <returns>True when the database was made, false when it already existed.</returns>
    public static async Task<bool> CreateDatabaseAsync(string adminConnection, string database, CancellationToken ct = default)
    {
        if (!DatabaseRule().IsMatch(database))
            throw new ArgumentException("Not a usable database name.", nameof(database)); // the name goes into the statement, so it is checked, then quoted
        await using var source = NpgsqlDataSource.Create(adminConnection);
        await using (var exists = source.CreateCommand("SELECT 1 FROM pg_database WHERE datname = $1"))
        {
            exists.Parameters.AddWithValue(database);
            if (await exists.ExecuteScalarAsync(ct) is not null)
                return false;
        }

        await using var create = source.CreateCommand($"CREATE DATABASE \"{database}\" ENCODING 'UTF8'");
        await create.ExecuteNonQueryAsync(ct);
        return true;
    }
}
