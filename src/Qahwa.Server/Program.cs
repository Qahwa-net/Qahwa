using Microsoft.Extensions.DependencyInjection.Extensions;
using Qahwa.Api;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
using Qahwa.Data.Postgres;
using Qahwa.Hosting;

var builder = WebApplication.CreateBuilder(args);

// The main site is described by the host's own settings; extra sites (Qahwa:Sites:Path) each get a container of their own.
builder.Services.AddQahwaSite(builder.Configuration, builder.Environment, null);

var sitesPath = builder.Configuration["Qahwa:Sites:Path"];
var siteDefinitions = string.IsNullOrWhiteSpace(sitesPath) ? [] : SiteCatalog.Load(Path.GetFullPath(sitesPath, builder.Environment.ContentRootPath));
var mainSiteServes = siteDefinitions.Count == 0 || !string.IsNullOrWhiteSpace(builder.Configuration.GetConnectionString("Default"));
builder.Services.AddSingleton(sp => new SiteHost(siteDefinitions, sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<IHostEnvironment>(), sp.GetRequiredService<ILoggerFactory>(),
    sp.GetServices<ISiteServicesCustomizer>(), mainSiteServes));
builder.Services.AddHostedService(sp => sp.GetRequiredService<SiteHost>());
if (mainSiteServes)
    builder.Services.AddHostedService<SchedulerService>();

var app = builder.Build();

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers.Append("Referrer-Policy", "same-origin");
    // The client-script sandbox is a worker that must be allowed to build functions from text, so it gets a policy of its own: nothing
    // else (no network of any kind, no frames, no styles) is allowed to it, and it has no page to attack.
    headers.Append("Content-Security-Policy", context.Request.Path.Equals("/client-script-worker.js", StringComparison.Ordinal)
        ? "default-src 'none'; script-src 'self' 'unsafe-eval'; connect-src 'none'"
        : "default-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'");
    if (context.Request.Path.StartsWithSegments("/api"))
        headers.CacheControl = "no-store"; // API responses can contain private data

    await next();
});

// The built Desk (src/Qahwa.Desk) is served from wwwroot.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseQahwaSites();
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

var autoMigrate = app.Configuration.GetValue<bool>("Qahwa:AutoMigrate");
if (mainSiteServes)
    await SiteStartup.RunAsync(app.Services, app.Logger, autoMigrate);
await app.Services.GetRequiredService<SiteHost>().PrepareAsync(app.Services.GetRequiredService<ILoggerFactory>(), autoMigrate);

app.Run();

// Exposed for WebApplicationFactory in integration tests.
public partial class Program;
