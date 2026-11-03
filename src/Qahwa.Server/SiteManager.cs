using System.Text.Json;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Data.Postgres;
using Qahwa.Data.SqlServer;
using Qahwa.Hosting;

/// <summary>Makes the database of a new site (a seam so tests need no database server).</summary>
public interface ISiteDatabases
{
    /// <returns>The connection string of the site's database, and whether the database was just made (false: it already existed).</returns>
    Task<(string Connection, bool Made)> CreateAsync(string provider, string adminConnection, string site, CancellationToken ct);
}

public sealed class SiteDatabases : ISiteDatabases
{
    public async Task<(string Connection, bool Made)> CreateAsync(string provider, string adminConnection, string site, CancellationToken ct)
    {
        if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
        {
            var name = SqlServerSites.DatabaseNameFor(site);
            return (SqlServerSites.ConnectionFor(adminConnection, name), await SqlServerSites.CreateDatabaseAsync(adminConnection, name, ct));
        }

        var database = PostgresSites.DatabaseNameFor(site);
        return (PostgresSites.ConnectionFor(adminConnection, database), await PostgresSites.CreateDatabaseAsync(adminConnection, database, ct));
    }
}

public sealed record NewSiteRequest(string? Name, IReadOnlyList<string>? Hosts, string? AdminEmail, string? AdminPassword, string? AdminName);

public sealed record SiteSummary(string Name, IReadOnlyList<string> Hosts);

/// <summary>
/// Adds and removes extra sites while the server runs (ADR-148). A new site gets what <c>qahwa new-site</c> gives it: a database of its
/// own, a <c>site_config.json</c> and an administrator, and starts answering at once. Removing a site only stops serving it: its
/// database and files are never deleted, and its settings file is renamed so a restart does not bring it back.
/// </summary>
public sealed class SiteManager(SiteHost host, IConfiguration config, IHostEnvironment env, ISiteDatabases databases, ILogger<SiteManager> logger)
{
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <summary>On only when the operator asked for it (<c>Qahwa:Sites:Manage</c>) and there is a folder to keep sites in.</summary>
    public bool Enabled => config.GetValue<bool>("Qahwa:Sites:Manage") && SitesPath is not null;

    private string? SitesPath => string.IsNullOrWhiteSpace(config["Qahwa:Sites:Path"]) ? null : Path.GetFullPath(config["Qahwa:Sites:Path"]!, env.ContentRootPath);

    public IReadOnlyList<SiteSummary> List() =>
        host.Sites.OrderBy(s => s.Name, StringComparer.Ordinal).Select(s => new SiteSummary(s.Name, s.Definition.Hosts)).ToList();

    public async Task<SiteSummary> CreateAsync(NewSiteRequest request, CancellationToken ct)
    {
        var folderRoot = SitesPath ?? throw new InvalidOperationException("Sites are not set up.");
        var errors = new List<FieldError>();
        var name = request.Name?.Trim();
        if (!SiteCatalog.ValidName(name))
            errors.Add(new FieldError("name", "Use lower-case letters, digits and dashes (up to 40)."));
        var hosts = new List<string>();
        foreach (var raw in request.Hosts ?? [])
        {
            if (SiteCatalog.NormalizeHost(raw, allowWildcard: true) is { } normalized)
            {
                if (!hosts.Contains(normalized))
                    hosts.Add(normalized);
            }
            else
            {
                errors.Add(new FieldError("hosts", $"'{raw}' is not a valid host name."));
            }
        }

        if (hosts.Count == 0 && errors.All(e => e.Field != "hosts"))
            errors.Add(new FieldError("hosts", "At least one host name is required."));
        if (AuthService.Normalize(request.AdminEmail) is null)
            errors.Add(new FieldError("adminEmail", "A valid email is required for the first administrator."));
        if (string.IsNullOrEmpty(request.AdminPassword))
            errors.Add(new FieldError("adminPassword", "A password is required for the first administrator."));
        var adminConnection = config["Qahwa:Sites:AdminConnection"];
        if (string.IsNullOrWhiteSpace(adminConnection))
            throw new DocumentValidationException("The server has no Qahwa:Sites:AdminConnection, the login that may create databases.");
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        await _one.WaitAsync(ct);
        try
        {
            var folder = Path.Combine(folderRoot, name!);
            var file = Path.Combine(folder, SiteCatalog.ConfigFile);
            if (Directory.Exists(folder))
                throw new ConflictException($"The site '{name}' already exists in the sites folder.");
            var provider = config["Qahwa:Database:Provider"] ?? "Postgres";

            // Nothing is made until the new site fits with the running ones; the database comes first because its name is the check.
            var (connection, made) = await databases.CreateAsync(provider, adminConnection, name!, ct);
            var definition = new SiteDefinition(name!, hosts, connection, file);
            var clash = SiteCatalog.Check([.. host.Sites.Select(s => s.Definition), definition]).ToList();
            if (clash.Count > 0)
                throw new DocumentValidationException(string.Join(" ", clash));

            Directory.CreateDirectory(folder);
            var settings = new Dictionary<string, object?> { ["hosts"] = hosts, ["connectionString"] = connection };
            if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
                settings["Qahwa"] = new Dictionary<string, object?> { ["Database"] = new Dictionary<string, object?> { ["Provider"] = "SqlServer" } };
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine, ct);
            try
            {
                var site = await host.AddAsync(definition, autoMigrate: config.GetValue("Qahwa:Sites:MigrateNew", true), ct);
                await site.Provider.GetRequiredService<AuthService>().EnsureUserAsync(
                    request.AdminEmail!, string.IsNullOrWhiteSpace(request.AdminName) ? "Administrator" : request.AdminName.Trim(), request.AdminPassword!, ["Administrator", "System Manager"], ct);
            }
            catch
            {
                // Undo what this call did to the running server and the folder; a database that was just made is kept (and named in the log).
                await host.RemoveAsync(name!, CancellationToken.None);
                Directory.Delete(folder, recursive: true);
                if (made)
                    logger.LogWarning("Site '{Site}' could not be set up; its new database was left in place.", name);
                throw;
            }

            logger.LogInformation("Site '{Site}' added for {Hosts}.", name, string.Join(", ", hosts));
            return new SiteSummary(name!, hosts);
        }
        finally
        {
            _one.Release();
        }
    }

    public async Task RemoveAsync(string name, CancellationToken ct)
    {
        var folderRoot = SitesPath ?? throw new InvalidOperationException("Sites are not set up.");
        await _one.WaitAsync(ct);
        try
        {
            var running = host.Sites.FirstOrDefault(s => s.Name == name) ?? throw new NotFoundException($"No site '{name}' is running.");
            var file = running.Definition.ConfigPath;
            if (!await host.RemoveAsync(name, ct))
                throw new NotFoundException($"No site '{name}' is running.");
            if (File.Exists(file) && Path.GetFullPath(file).StartsWith(folderRoot, StringComparison.Ordinal))
                File.Move(file, file + ".removed-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"));
            logger.LogInformation("Site '{Site}' removed; its database and files were not touched.", name);
        }
        finally
        {
            _one.Release();
        }
    }
}
