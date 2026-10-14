using Microsoft.Extensions.DependencyInjection;
using Qahwa.Data.Postgres;
using Qahwa.Hosting;

const string usage = """
    qahwa migrate --connection <connection-string> --doctypes <dir> [--doctypes <dir> ...] [--plan]

      Creates or updates database tables for every DocType found in the given directories.
      --plan   print the SQL that would run without executing it
      The connection string may also be provided through the QAHWA_CONNECTION environment variable.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? 1 : 0;
}

if (args[0] != "migrate")
{
    Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{usage}");
    return 1;
}

var connection = Environment.GetEnvironmentVariable("QAHWA_CONNECTION");
var doctypePaths = new List<string>();
var planOnly = false;

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
        case "--plan":
            planOnly = true;
            break;
        default:
            Console.Error.WriteLine($"Unexpected argument '{args[i]}'.\n\n{usage}");
            return 1;
    }
}

if (string.IsNullOrWhiteSpace(connection) || doctypePaths.Count == 0)
{
    Console.Error.WriteLine($"Both a connection string and at least one --doctypes directory are required.\n\n{usage}");
    return 1;
}

var services = new ServiceCollection();
services.Configure<QahwaOptions>(o => o.DocTypePaths.AddRange(doctypePaths));
services.AddQahwa();
services.AddQahwaPostgres(_ => connection);

await using var provider = services.BuildServiceProvider();
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
