using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core.Auth;
using Qahwa.Data.Postgres;
using Qahwa.Data.SqlServer;
using Qahwa.Hosting;

const string usage = """
    qahwa migrate      (--connection <cs> | --site <name> --sites <dir>) --doctypes <dir> [--doctypes <dir> ...] [--plan]
    qahwa create-admin (--connection <cs> | --site <name> --sites <dir>) --email <email> [--full-name <name>]
                       [--password-stdin] [--doctypes <dir> ...]
    qahwa new-site     <name> --sites <dir> --host <host> [--host <host> ...] --admin-connection <cs>
                       [--provider postgres|sqlserver] [--connection-env <VARIABLE>] [--doctypes <dir> ...] [--email <email> [--full-name <name>] [--password-stdin]]
    qahwa list-sites   --sites <dir>
    qahwa list-apps    (--connection <cs> | --site <name> --sites <dir>)
    qahwa install-app  <app> (--connection <cs> | --site <name> --sites <dir>)
    qahwa uninstall-app <app> (--connection <cs> | --site <name> --sites <dir>)
    qahwa backup       (--connection <cs> | --site <name> --sites <dir>) --out <file.zip> [--doctypes <dir> ...]
    qahwa restore      <file.zip> (--connection <cs> | --site <name> --sites <dir>) [--dry-run] [--doctypes <dir> ...]

    migrate       Creates or updates database tables for the framework and every DocType found in the directories.
                  --plan   print the SQL that would run without executing it
    create-admin  Creates (or re-enables) a user with the Administrator and System Manager roles and sets its
                  password. Run `migrate` first. The password is prompted for, or read from stdin with --password-stdin.
                  It is never accepted as a command-line argument.
    new-site      Makes a site: its own database (qahwa_<name>) on the server of --admin-connection, a folder
                  <sites>/<name>/ with site_config.json, the tables, and (with --email) an administrator. With
                  --connection-env the file holds only the name of an environment variable for the connection string,
                  which you must set when the server runs. Run the server with Qahwa:Sites:Path set to <sites>.
    list-sites    Shows the sites found in the directory.
    list-apps     Shows the apps the server has recorded for the site and whether each is installed or uninstalled.
    install-app   Switches an app back on for the site. The app must have been recorded by a server start.
    uninstall-app Switches an app off for the site (its hooks, methods, jobs and reports stop; its data and DocTypes stay).
                  A running server notices within a few minutes; apps that need this one are switched off too.

    backup        Writes the documents and definitions of the site to a zip (never passwords, sessions or secrets). The file must not exist yet.
    restore       Adds what is in a backup zip to the site: rows that already exist are left alone. --dry-run only reports what would be added.
                  Both need the same --doctypes the server uses, so the app DocTypes are known.

    --connection (or QAHWA_CONNECTION) names the database directly; --site names a site made by new-site.
    --provider chooses the database product: postgres (the default) or sqlserver (also QAHWA_PROVIDER). For an existing site
    it is read from the site's file.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
if (command is not ("migrate" or "create-admin" or "new-site" or "list-sites" or "list-apps" or "install-app" or "uninstall-app" or "backup" or "restore"))
{
    Console.Error.WriteLine($"Unknown command '{command}'.\n\n{usage}");
    return 1;
}

var connection = Environment.GetEnvironmentVariable("QAHWA_CONNECTION");
var doctypePaths = new List<string>();
var hosts = new List<string>();
var product = (Environment.GetEnvironmentVariable("QAHWA_PROVIDER") ?? "postgres").ToLowerInvariant();
string? email = null, fullName = null, site = null, sitesDir = null, adminConnection = null, connectionEnv = null, siteName = null, appName = null;
var planOnly = false;
var dryRun = false;
string? outFile = null, restoreFile = null;
var passwordFromStdin = false;

for (var i = 1; i < args.Length; i++)
{
    var arg = args[i];
    var hasValue = i + 1 < args.Length;
    switch (arg)
    {
        case "--connection" when hasValue && command is "migrate" or "create-admin" or "list-apps" or "install-app" or "uninstall-app" or "backup" or "restore" or "backup" or "restore":
            connection = args[++i];
            break;
        case "--doctypes" when hasValue && command != "list-sites":
            doctypePaths.Add(Path.GetFullPath(args[++i]));
            break;
        case "--email" when hasValue && command is "create-admin" or "new-site":
            email = args[++i];
            break;
        case "--full-name" when hasValue && command is "create-admin" or "new-site":
            fullName = args[++i];
            break;
        case "--password-stdin" when command is "create-admin" or "new-site":
            passwordFromStdin = true;
            break;
        case "--out" when hasValue && command == "backup":
            outFile = args[++i];
            break;
        case "--dry-run" when command == "restore":
            dryRun = true;
            break;
        case "--plan" when command == "migrate":
            planOnly = true;
            break;
        case "--site" when hasValue && command is "migrate" or "create-admin" or "list-apps" or "install-app" or "uninstall-app" or "backup" or "restore" or "backup" or "restore":
            site = args[++i];
            break;
        case "--provider" when hasValue && command is "migrate" or "create-admin" or "new-site":
            product = args[++i].ToLowerInvariant();
            break;
        case "--sites" when hasValue:
            sitesDir = Path.GetFullPath(args[++i]);
            break;
        case "--host" when hasValue && command == "new-site":
            hosts.Add(args[++i]);
            break;
        case "--admin-connection" when hasValue && command == "new-site":
            adminConnection = args[++i];
            break;
        case "--connection-env" when hasValue && command == "new-site":
            connectionEnv = args[++i];
            break;
        default:
            if (command == "new-site" && siteName is null && !arg.StartsWith('-'))
            {
                siteName = arg;
                break;
            }

            if (command == "restore" && restoreFile is null && !arg.StartsWith('-'))
            {
                restoreFile = arg;
                break;
            }

            if (command is "install-app" or "uninstall-app" && appName is null && !arg.StartsWith('-'))
            {
                appName = arg;
                break;
            }

            Console.Error.WriteLine($"Unexpected argument '{arg}'.\n\n{usage}");
            return 1;
    }
}

try
{
    if (command == "list-sites")
    {
        if (string.IsNullOrWhiteSpace(sitesDir))
            return Fail("--sites is required.");
        var sites = SiteCatalog.Load(sitesDir);
        foreach (var s in sites)
            Console.WriteLine($"{s.Name}: {string.Join(", ", s.Hosts)}");
        Console.WriteLine(sites.Count == 0 ? "No sites." : $"{sites.Count} site(s).");
        return 0;
    }

    if (command == "new-site")
        return await NewSiteAsync();

    if (!string.IsNullOrWhiteSpace(site))
    {
        if (string.IsNullOrWhiteSpace(sitesDir))
            return Fail("--sites is required with --site.");
        var found = SiteCatalog.Load(sitesDir).FirstOrDefault(s => s.Name == site)
            ?? throw new InvalidOperationException($"There is no site '{site}' in {sitesDir}.");
        connection = found.ConnectionString;
        using (var file = JsonDocument.Parse(File.ReadAllText(found.ConfigPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
        {
            if (file.RootElement.TryGetProperty("Qahwa", out var q) && q.TryGetProperty("Database", out var d) && d.TryGetProperty("Provider", out var p) && p.GetString() is { } named)
                product = named.ToLowerInvariant();
        }
    }

    if (string.IsNullOrWhiteSpace(connection))
        return Fail("A connection string (or --site) is required.");
    if (command == "migrate" && doctypePaths.Count == 0)
        Console.WriteLine("Note: no --doctypes given; only the built-in framework DocTypes will be migrated.");
    if (command == "create-admin" && string.IsNullOrWhiteSpace(email))
        return Fail("--email is required.");
    if (command is "install-app" or "uninstall-app" && string.IsNullOrWhiteSpace(appName))
        return Fail("The name of the app is required.");
    if (command == "backup" && string.IsNullOrWhiteSpace(outFile))
        return Fail("--out is required.");
    if (command == "restore" && string.IsNullOrWhiteSpace(restoreFile))
        return Fail("The backup file is required.");

    await using var provider = Build(connection);
    return command switch
    {
        "migrate" => await MigrateAsync(provider),
        "list-apps" or "install-app" or "uninstall-app" => await AppsAsync(provider),
        "backup" => await BackupAsync(provider),
        "restore" => await RestoreAsync(provider),
        _ => await CreateAdminAsync(provider),
    };
}
catch (IOException e)
{
    return Fail(e.Message);
}
catch (SiteConfigException e)
{
    return Fail(e.Message);
}
catch (InvalidOperationException e)
{
    return Fail(e.Message);
}
catch (ArgumentException e)
{
    return Fail(e.Message);
}

int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

ServiceProvider Build(string connectionString)
{
    var services = new ServiceCollection();
    services.Configure<QahwaOptions>(o => o.DocTypePaths.AddRange(doctypePaths));
    services.AddQahwa();
    if (product == "sqlserver")
        services.AddQahwaSqlServer(_ => connectionString);
    else
        services.AddQahwaPostgres(_ => connectionString);
    return services.BuildServiceProvider();
}

async Task<int> MigrateAsync(ServiceProvider provider)
{
    var result = await provider.GetRequiredService<QahwaMigrator>().MigrateAsync(planOnly);
    foreach (var plan in result.Plans)
    {
        Console.WriteLine($"{plan.DocType}: {(plan.IsEmpty ? "up to date" : $"{plan.Statements.Count} change(s)")}");
        foreach (var statement in plan.Statements)
            Console.WriteLine($"  {(planOnly ? "would run" : "ran")}: {statement.Sql}");
        foreach (var warning in plan.Warnings)
            Console.WriteLine($"  WARNING: {warning}");
    }

    return 0;
}

async Task<int> AppsAsync(ServiceProvider provider)
{
    var registry = provider.GetRequiredService<Qahwa.Core.IMetaRegistry>();
    var repository = provider.GetRequiredService<Qahwa.Core.IDocumentRepository>();
    if (!registry.TryGet("Installed App", out var meta))
        return Fail("The framework DocTypes are not loaded.");
    var rows = await repository.ListAsync(meta, new Qahwa.Core.ListQuery { Fields = ["name", "version", "status"], OrderBy = "name", Descending = false, Limit = 1000 });
    string StatusOf(Dictionary<string, object?> row) => row.GetValueOrDefault("status") as string == "Uninstalled" ? "uninstalled" : "installed";

    if (command == "list-apps")
    {
        foreach (var row in rows)
            Console.WriteLine($"{row["name"]} {row.GetValueOrDefault("version")}: {StatusOf(row)}");
        Console.WriteLine(rows.Count == 0 ? "No apps are recorded. Start the server once so it records the apps it has." : $"{rows.Count} app(s).");
        return 0;
    }

    var found = rows.FirstOrDefault(r => string.Equals(r["name"] as string, appName, StringComparison.Ordinal));
    if (found is null)
        return Fail($"The app '{appName}' is not recorded for this site. Start the server once so it records its apps.");
    var target = command == "install-app" ? "Installed" : "Uninstalled";
    if (StatusOf(found) == target.ToLowerInvariant())
    {
        Console.WriteLine($"'{appName}' is already {target.ToLowerInvariant()}.");
        return 0;
    }

    var stored = (DateTime)(await repository.GetAsync(meta, appName!))!["modified"]!;
    var now = DateTime.UtcNow > stored ? DateTime.UtcNow : stored.AddTicks(10);
    if (!await repository.UpdateAsync(meta, appName!, new Dictionary<string, object?> { ["status"] = target, ["modified"] = now, ["modified_by"] = "Administrator" }, stored))
        return Fail("The record was changed at the same time. Try again.");
    Console.WriteLine($"'{appName}' is now {target.ToLowerInvariant()}. A running server applies this within a few minutes.");
    return 0;
}

async Task<int> BackupAsync(ServiceProvider provider)
{
    var path = Path.GetFullPath(outFile!);
    var admin = new Qahwa.Core.UserContext(Qahwa.Core.UserContext.AdministratorRole, [Qahwa.Core.UserContext.AdministratorRole]);
    try
    {
        // CreateNew: an existing file is never overwritten.
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await provider.GetRequiredService<Qahwa.Core.BackupService>().WriteAsync(stream, admin);
    }
    catch
    {
        if (File.Exists(path) && new FileInfo(path).Length == 0)
            File.Delete(path);
        throw;
    }

    Console.WriteLine($"Backup written to {path} ({new FileInfo(path).Length} bytes). It holds business data: keep it private.");
    return 0;
}

async Task<int> RestoreAsync(ServiceProvider provider)
{
    var path = Path.GetFullPath(restoreFile!);
    if (!File.Exists(path))
        return Fail($"There is no file {path}.");
    var admin = new Qahwa.Core.UserContext(Qahwa.Core.UserContext.AdministratorRole, [Qahwa.Core.UserContext.AdministratorRole]);
    await using var stream = File.OpenRead(path);
    var report = await provider.GetRequiredService<Qahwa.Core.BackupService>().RestoreAsync(stream, dryRun, admin);
    Console.WriteLine(report.DryRun ? "Dry run: nothing was changed." : "Restored.");
    foreach (var table in report.Tables)
        Console.WriteLine($"  {table}");
    Console.WriteLine($"Definitions: {report.Definitions.Count}; counters raised: {report.SeriesRaised}.");
    return 0;
}

async Task<int> CreateAdminAsync(ServiceProvider provider)
{
    var password = passwordFromStdin ? Console.In.ReadLine() : PromptForPassword();
    if (string.IsNullOrEmpty(password))
        return Fail("No password provided.");

    try
    {
        await provider.GetRequiredService<AuthService>().EnsureUserAsync(
            email!,
            string.IsNullOrWhiteSpace(fullName) ? "Administrator" : fullName,
            password,
            ["Administrator", "System Manager"]);
        Console.WriteLine($"Administrator '{AuthService.Normalize(email)}' is ready.");
        return 0;
    }
    catch (Qahwa.Core.DocumentValidationException ex)
    {
        foreach (var error in ex.Errors)
            Console.Error.WriteLine($"{error.Field}: {error.Message}");
        return 1;
    }
}

async Task<int> NewSiteAsync()
{
    if (string.IsNullOrWhiteSpace(siteName) || !SiteCatalog.ValidName(siteName))
        return Fail("A site name is required: lower-case letters, digits and dashes (up to 40).");
    if (string.IsNullOrWhiteSpace(sitesDir) || hosts.Count == 0 || string.IsNullOrWhiteSpace(adminConnection))
        return Fail("--sites, at least one --host and --admin-connection are required.");
    var normalized = hosts.Select(h => SiteCatalog.NormalizeHost(h, allowWildcard: true) ?? throw new ArgumentException($"'{h}' is not a valid host name.")).Distinct().ToList();
    if (connectionEnv is not null && !System.Text.RegularExpressions.Regex.IsMatch(connectionEnv, "^[A-Za-z_][A-Za-z0-9_]*$"))
        return Fail("--connection-env must be the name of an environment variable.");

    // Nothing is created until the new site fits with the others (no shared host names or databases).
    var folder = Path.Combine(sitesDir, siteName);
    if (Directory.Exists(folder))
        return Fail($"The site '{siteName}' already exists in {sitesDir}.");
    if (product is not ("postgres" or "sqlserver"))
        return Fail("--provider must be postgres or sqlserver.");
    var sqlServer = product == "sqlserver";
    var database = sqlServer ? SqlServerSites.DatabaseNameFor(siteName) : PostgresSites.DatabaseNameFor(siteName);
    var siteConnection = sqlServer ? SqlServerSites.ConnectionFor(adminConnection, database) : PostgresSites.ConnectionFor(adminConnection, database);
    var existing = SiteCatalog.Load(sitesDir).ToList();
    var problems = SiteCatalog.Check([.. existing, new SiteDefinition(siteName, normalized, siteConnection, Path.Combine(folder, SiteCatalog.ConfigFile))]).ToList();
    if (problems.Count > 0)
        return Fail(string.Join("\n", problems));

    string? password = null;
    if (!string.IsNullOrWhiteSpace(email))
    {
        password = passwordFromStdin ? Console.In.ReadLine() : PromptForPassword();
        if (string.IsNullOrEmpty(password))
            return Fail("No password provided.");
    }

    var made = sqlServer ? await SqlServerSites.CreateDatabaseAsync(adminConnection, database) : await PostgresSites.CreateDatabaseAsync(adminConnection, database);
    Console.WriteLine(made ? $"Database {database} created." : $"Database {database} already existed; it will be used.");

    Directory.CreateDirectory(folder);
    var config = new Dictionary<string, object?> { ["hosts"] = normalized };
    if (sqlServer)
        config["Qahwa"] = new Dictionary<string, object?> { ["Database"] = new Dictionary<string, object?> { ["Provider"] = "SqlServer" } };
    if (connectionEnv is null)
        config["connectionString"] = siteConnection;
    else
        config["connectionStringEnv"] = connectionEnv;
    await File.WriteAllTextAsync(Path.Combine(folder, SiteCatalog.ConfigFile), JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine($"Wrote {Path.Combine(folder, SiteCatalog.ConfigFile)}" + (connectionEnv is null ? " (it holds the database password: keep it private)." : $" (set {connectionEnv} to the connection string when the server runs)."));

    await using var provider = Build(siteConnection);
    await MigrateAsync(provider);
    if (password is not null)
    {
        passwordFromStdin = true;
        var stdin = Console.In;
        Console.SetIn(new StringReader(password + Environment.NewLine));
        var code = await CreateAdminAsync(provider);
        Console.SetIn(stdin);
        if (code != 0)
            return code;
    }

    Console.WriteLine($"Site '{siteName}' is ready for {string.Join(", ", normalized)}.");
    return 0;
}

static string? PromptForPassword()
{
    if (Console.IsInputRedirected)
    {
        Console.Error.WriteLine("Input is redirected; use --password-stdin.");
        return null;
    }

    Console.Write("Password: ");
    var first = ReadHidden();
    Console.Write("Repeat password: ");
    var second = ReadHidden();
    if (first == second)
        return first;

    Console.Error.WriteLine("Passwords do not match.");
    return null;
}

static string ReadHidden()
{
    var buffer = new System.Text.StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
            buffer.Length--;
        else if (!char.IsControl(key.KeyChar))
            buffer.Append(key.KeyChar);
    }

    Console.WriteLine();
    return buffer.ToString();
}
