using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core.Auth;
using Qahwa.Data.Postgres;
using Qahwa.Hosting;

const string usage = """
    qahwa migrate      (--connection <cs> | --site <name> --sites <dir>) --doctypes <dir> [--doctypes <dir> ...] [--plan]
    qahwa create-admin (--connection <cs> | --site <name> --sites <dir>) --email <email> [--full-name <name>]
                       [--password-stdin] [--doctypes <dir> ...]
    qahwa new-site     <name> --sites <dir> --host <host> [--host <host> ...] --admin-connection <cs>
                       [--connection-env <VARIABLE>] [--doctypes <dir> ...] [--email <email> [--full-name <name>] [--password-stdin]]
    qahwa list-sites   --sites <dir>

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

    --connection (or QAHWA_CONNECTION) names the database directly; --site names a site made by new-site.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
if (command is not ("migrate" or "create-admin" or "new-site" or "list-sites"))
{
    Console.Error.WriteLine($"Unknown command '{command}'.\n\n{usage}");
    return 1;
}

var connection = Environment.GetEnvironmentVariable("QAHWA_CONNECTION");
var doctypePaths = new List<string>();
var hosts = new List<string>();
string? email = null, fullName = null, site = null, sitesDir = null, adminConnection = null, connectionEnv = null, siteName = null;
var planOnly = false;
var passwordFromStdin = false;

for (var i = 1; i < args.Length; i++)
{
    var arg = args[i];
    var hasValue = i + 1 < args.Length;
    switch (arg)
    {
        case "--connection" when hasValue && command is "migrate" or "create-admin":
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
        case "--plan" when command == "migrate":
            planOnly = true;
            break;
        case "--site" when hasValue && command is "migrate" or "create-admin":
            site = args[++i];
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
        connection = SiteCatalog.Load(sitesDir).FirstOrDefault(s => s.Name == site)?.ConnectionString
            ?? throw new InvalidOperationException($"There is no site '{site}' in {sitesDir}.");
    }

    if (string.IsNullOrWhiteSpace(connection))
        return Fail("A connection string (or --site) is required.");
    if (command == "migrate" && doctypePaths.Count == 0)
        Console.WriteLine("Note: no --doctypes given; only the built-in framework DocTypes will be migrated.");
    if (command == "create-admin" && string.IsNullOrWhiteSpace(email))
        return Fail("--email is required.");

    await using var provider = Build(connection);
    return command == "migrate" ? await MigrateAsync(provider) : await CreateAdminAsync(provider);
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
    var database = PostgresSites.DatabaseNameFor(siteName);
    var siteConnection = PostgresSites.ConnectionFor(adminConnection, database);
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

    var made = await PostgresSites.CreateDatabaseAsync(adminConnection, database);
    Console.WriteLine(made ? $"Database {database} created." : $"Database {database} already existed; it will be used.");

    Directory.CreateDirectory(folder);
    var config = new Dictionary<string, object?> { ["hosts"] = normalized };
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
