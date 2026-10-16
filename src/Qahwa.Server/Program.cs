using Qahwa.Api;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
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

var emailSettings = builder.Configuration.GetSection("Qahwa:Email").Get<EmailSettings>() ?? new EmailSettings();
builder.Services.AddSingleton(emailSettings);
if (string.IsNullOrWhiteSpace(emailSettings.Host))
    builder.Services.AddSingleton<IEmailSender, LogEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
var webhookOptions = builder.Configuration.GetSection("Qahwa:Webhooks").Get<WebhookOptions>() ?? new WebhookOptions();
builder.Services.AddSingleton(webhookOptions);
builder.Services.AddSingleton<IWebhookSender, WebhookSender>();
var fileOptions = builder.Configuration.GetSection("Qahwa:Files").Get<AttachmentOptions>() ?? new AttachmentOptions();
fileOptions.MaxMegabytes = Math.Clamp(fileOptions.MaxMegabytes, 1, 100);
builder.Services.AddSingleton(fileOptions);
builder.Services.AddSingleton<IFileStorage>(new DiskFileStorage(builder.Configuration["Qahwa:Files:Path"] ?? Path.Combine(builder.Environment.ContentRootPath, "data", "files")));
builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers.Append("Referrer-Policy", "same-origin");
    headers.Append("Content-Security-Policy",
        "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'");
    if (context.Request.Path.StartsWithSegments("/api"))
        headers.CacheControl = "no-store"; // API responses can contain private data

    await next();
});

// The built Desk (src/Qahwa.Desk) is served from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseQahwa();
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", framework = "Qahwa" }));
app.MapQahwaApi();

// Unknown API paths get the JSON error envelope; every other unknown path is a Desk route handled by the SPA.
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsJsonAsync(new { code = "not_found", message = "No such endpoint." });
        return;
    }

    var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index);
});

if (app.Configuration.GetValue<bool>("Qahwa:AutoMigrate"))
{
    var result = await app.Services.GetRequiredService<QahwaMigrator>().MigrateAsync();
    foreach (var plan in result.Plans)
    {
        foreach (var warning in plan.Warnings)
            app.Logger.LogWarning("Schema: {Warning}", warning);
    }
}

// DocTypes created in the designer live in the database; load them (a no-op if already loaded by the migration).
foreach (var failure in await app.Services.GetRequiredService<CustomDocTypeLoader>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

// Fields and properties customised in the Customize Form (a no-op if the migration already applied them).
foreach (var failure in await app.Services.GetRequiredService<CustomizationManager>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

// A mistake in Qahwa:Auth:AllowedIps must stop the server here, not quietly open or close the system.
_ = app.Services.GetRequiredService<IpAllowList>();

// Webhooks.
foreach (var failure in await app.Services.GetRequiredService<WebhookManager>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

// Notification rules.
foreach (var failure in await app.Services.GetRequiredService<NotificationRuleManager>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

// Assignment rules.
foreach (var failure in await app.Services.GetRequiredService<AssignmentRuleManager>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

// Workflows built in the Workflow Builder.
foreach (var failure in await app.Services.GetRequiredService<WorkflowManager>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

// Permission rules customised in the Role Permission Manager.
foreach (var failure in await app.Services.GetRequiredService<PermissionManager>().LoadAsync())
    app.Logger.LogError("{Failure}", failure);

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
