using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core.Auth;
using Qahwa.Data.Postgres;
using Qahwa.Hosting;

const string usage = """
    qahwa migrate      --connection <cs> --doctypes <dir> [--doctypes <dir> ...] [--plan]
    qahwa create-admin --connection <cs> --email <email> [--full-name <name>] [--password-stdin]
                       [--doctypes <dir> ...]

    migrate       Creates or updates database tables for the framework and every DocType found in the directories.
                  --plan   print the SQL that would run without executing it
    create-admin  Creates (or re-enables) a user with the Administrator and System Manager roles and sets its
                  password. Run `migrate` first. The password is prompted for, or read from stdin with --password-stdin.
                  It is never accepted as a command-line argument.

    The connection string may also be provided through the QAHWA_CONNECTION environment variable.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
if (command is not ("migrate" or "create-admin"))
{
    Console.Error.WriteLine($"Unknown command '{command}'.\n\n{usage}");
    return 1;
}

var connection = Environment.GetEnvironmentVariable("QAHWA_CONNECTION");
var doctypePaths = new List<string>();
string? email = null, fullName = null;
var planOnly = false;
var passwordFromStdin = false;

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--connection" when i + 1 < args.Length:
            connection = args[++i];
            break;
        case "--doctypes" when i + 1 < args.Length:
            doctypePaths.Add(Path.GetFullPath(args[++i]));
            break;
        case "--email" when i + 1 < args.Length && command == "create-admin":
            email = args[++i];
            break;
        case "--full-name" when i + 1 < args.Length && command == "create-admin":
            fullName = args[++i];
            break;
        case "--password-stdin" when command == "create-admin":
            passwordFromStdin = true;
            break;
        case "--plan" when command == "migrate":
            planOnly = true;
            break;
        default:
            Console.Error.WriteLine($"Unexpected argument '{args[i]}'.\n\n{usage}");
            return 1;
    }
}

if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine($"A connection string is required.\n\n{usage}");
    return 1;
}

if (command == "migrate" && doctypePaths.Count == 0)
    Console.WriteLine("Note: no --doctypes given; only the built-in framework DocTypes will be migrated.");

if (command == "create-admin" && string.IsNullOrWhiteSpace(email))
{
    Console.Error.WriteLine($"--email is required.\n\n{usage}");
    return 1;
}

var services = new ServiceCollection();
services.Configure<QahwaOptions>(o => o.DocTypePaths.AddRange(doctypePaths));
services.AddQahwa();
services.AddQahwaPostgres(_ => connection);

await using var provider = services.BuildServiceProvider();

if (command == "migrate")
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

var password = passwordFromStdin ? Console.In.ReadLine() : PromptForPassword();
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("No password provided.");
    return 1;
}

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
