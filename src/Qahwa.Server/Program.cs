using Qahwa.Api;
using Qahwa.Core.Auth;
using Qahwa.Data.Postgres;
using Qahwa.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<QahwaOptions>().Configure<IConfiguration, IHostEnvironment>((options, config, env) =>
{
    foreach (var path in config.GetSection("Qahwa:DocTypePaths").Get<string[]>() ?? [])
        options.DocTypePaths.Add(Path.GetFullPath(path, env.ContentRootPath));
});
builder.Services.AddOptions<AuthOptions>().BindConfiguration("Qahwa:Auth");

builder.Services.AddQahwa();
builder.Services.AddQahwaPostgres(sp =>
    sp.GetRequiredService<IConfiguration>().GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."));
builder.Services.AddQahwaApi();

var app = builder.Build();

app.UseQahwa();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", framework = "Qahwa" }));
app.MapQahwaApi();

if (app.Configuration.GetValue<bool>("Qahwa:AutoMigrate"))
{
    var result = await app.Services.GetRequiredService<QahwaMigrator>().MigrateAsync();
    foreach (var plan in result.Plans)
    {
        foreach (var warning in plan.Warnings)
            app.Logger.LogWarning("Schema: {Warning}", warning);
    }
}

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
