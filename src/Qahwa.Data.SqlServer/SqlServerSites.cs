using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace Qahwa.Data.SqlServer;

/// <summary>Creating the database of a new site (<c>qahwa new-site --provider sqlserver</c>).</summary>
public static partial class SqlServerSites
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex SiteRule();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex DatabaseRule();

    /// <summary>The database for a site: <c>qahwa_</c> and the site name with dashes made underscores.</summary>
    public static string DatabaseNameFor(string site) =>
        site is not null && SiteRule().IsMatch(site) ? "qahwa_" + site.Replace('-', '_') : throw new ArgumentException("A site name is lower-case letters, digits and dashes (up to 40).", nameof(site));

    /// <summary>The connection string for <paramref name="database"/> on the same server and with the same login as <paramref name="admin"/>.</summary>
    public static string ConnectionFor(string admin, string database)
    {
        if (!DatabaseRule().IsMatch(database))
            throw new ArgumentException("Not a usable database name.", nameof(database));
        return new SqlConnectionStringBuilder(admin) { InitialCatalog = database }.ConnectionString;
    }

    /// <returns>True when the database was made, false when it already existed.</returns>
    public static async Task<bool> CreateDatabaseAsync(string adminConnection, string database, CancellationToken ct = default)
    {
        if (!DatabaseRule().IsMatch(database))
            throw new ArgumentException("Not a usable database name.", nameof(database)); // the name goes into the statement, so it is checked, then quoted
        await using var connection = new SqlConnection(adminConnection);
        await connection.OpenAsync(ct);
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT 1 FROM sys.databases WHERE name = @name";
            exists.Parameters.AddWithValue("@name", database);
            if (await exists.ExecuteScalarAsync(ct) is not null)
                return false;
        }

        await using var create = connection.CreateCommand();
        create.CommandText = $"CREATE DATABASE [{database}]";
        await create.ExecuteNonQueryAsync(ct);
        return true;
    }
}
