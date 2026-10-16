using Microsoft.Extensions.DependencyInjection.Extensions;
using Qahwa.Hosting;

/// <summary>One running extra site.</summary>
public sealed class RunningSite(SiteDefinition definition, ServiceProvider provider)
{
    public SiteDefinition Definition { get; } = definition;

    public string Name => Definition.Name;

    public ServiceProvider Provider { get; } = provider;
}

/// <summary>
/// Runs several sites in one server. The main site is what the host's own settings describe (it answers every host name that no
/// other site claims, when it has a database). Each extra site, found under <c>Qahwa:Sites:Path</c>, gets a container of its own built
/// from the same registrations but its own database and settings (<c>site_config.json</c> over the shared configuration), and is chosen
/// by the Host header of the request. The Host header is only looked at exactly (never <c>X-Forwarded-Host</c> unless the host is set up
/// to rewrite it), a malformed or unknown name reaches no site, and a site's files, backups and links are kept apart from the others.
/// </summary>
public sealed class SiteHost : IHostedService, IAsyncDisposable
{
    private readonly Dictionary<string, RunningSite> _sites = new(StringComparer.Ordinal);
    private readonly SiteResolver _resolver;
    private readonly List<SchedulerService> _schedulers = [];

    public SiteHost(IReadOnlyList<SiteDefinition> definitions, IConfiguration shared, IHostEnvironment env, ILoggerFactory loggers, IEnumerable<ISiteServicesCustomizer> customizers, bool mainSiteServes)
    {
        _resolver = new SiteResolver(definitions);
        MainSiteServes = mainSiteServes;
        var customizerList = customizers.ToList();
        foreach (var definition in definitions)
        {
            var config = SiteConfiguration(definition, shared, env);
            var services = new ServiceCollection();
            services.AddSingleton(loggers);
            services.AddLogging();
            services.AddSingleton(config);
            services.AddSingleton(env);
            services.AddQahwaSite(config, env, definition.Name);
            foreach (var customizer in customizerList)
                customizer.Customize(definition.Name, services);
            _sites[definition.Name] = new RunningSite(definition, services.BuildServiceProvider());
        }
    }

    public IReadOnlyCollection<RunningSite> Sites => _sites.Values;

    /// <summary>True when the main site (the host's own database) answers hosts that no extra site claims.</summary>
    public bool MainSiteServes { get; }

    public bool IsEmpty => _resolver.IsEmpty;

    public RunningSite? Resolve(string? host) => _resolver.Resolve(host) is { } definition ? _sites[definition.Name] : null;

    /// <summary>The shared settings with this site's file on top, and the few settings that must never be shared between sites made specific to it.</summary>
    public static IConfiguration SiteConfiguration(SiteDefinition definition, IConfiguration shared, IHostEnvironment env)
    {
        // Defaults for what must never be shared between sites; the site's own file may say otherwise (but never change its database).
        var defaults = new Dictionary<string, string?>
        {
            // Files and backups go to a folder of the site's own: two sites in one folder would mix their documents.
            ["Qahwa:Files:Path"] = Path.Combine(shared["Qahwa:Files:Path"] ?? Path.Combine(env.ContentRootPath, "data", "files"), "sites", definition.Name),
        };
        if (shared["Qahwa:Backup:Path"] is { Length: > 0 } backups)
            defaults["Qahwa:Backup:Path"] = Path.Combine(backups, "sites", definition.Name);

        // Links in emails (sign-up, password reset, single sign-on) must lead to this site, never to the main one.
        var host = definition.Hosts.FirstOrDefault(h => !h.StartsWith("*.", StringComparison.Ordinal) && !h.StartsWith('['));
        defaults["Qahwa:Auth:PublicUrl"] = host is null ? null : $"https://{host}";

        return new ConfigurationBuilder()
            .AddConfiguration(shared)
            .AddInMemoryCollection(defaults)
            .AddJsonFile(definition.ConfigPath, optional: false, reloadOnChange: false)
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = definition.ConnectionString, ["Qahwa:Site:Name"] = definition.Name })
            .Build();
    }

    /// <summary>Brings each extra site up to date (schema, definitions, patches).</summary>
    public async Task PrepareAsync(ILoggerFactory loggers, bool autoMigrate)
    {
        foreach (var site in _sites.Values)
        {
            try
            {
                await SiteStartup.RunAsync(site.Provider, loggers.CreateLogger($"Qahwa.Site.{site.Name}"), autoMigrate);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                throw new InvalidOperationException($"Site '{site.Name}' could not start: {e.Message}", e);
            }
        }
    }

    // The schedulers (repeating documents, queued email, webhooks, backups) run once per site, in the background.
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var site in _sites.Values)
        {
            var scheduler = ActivatorUtilities.CreateInstance<SchedulerService>(site.Provider);
            _schedulers.Add(scheduler);
            await scheduler.StartAsync(cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var scheduler in _schedulers)
            await scheduler.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scheduler in _schedulers)
            scheduler.Dispose();
        foreach (var site in _sites.Values)
            await site.Provider.DisposeAsync();
    }
}

public static class SiteMiddleware
{
    public const string ItemKey = "qahwa.site";

    /// <summary>Chooses the container for the request from its Host header, before anything else that needs the site's services.</summary>
    public static IApplicationBuilder UseQahwaSites(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var sites = context.RequestServices.GetRequiredService<SiteHost>();
            if (sites.IsEmpty)
            {
                await next(context);
                return;
            }

            var site = sites.Resolve(context.Request.Host.Value);
            if (site is null)
            {
                if (sites.MainSiteServes || context.Request.Path.StartsWithSegments("/api/health"))
                {
                    await next(context);
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                await context.Response.WriteAsJsonAsync(new { code = "unknown_site", message = "No site answers to this address." });
                return;
            }

            using var scope = site.Provider.CreateScope();
            context.RequestServices = scope.ServiceProvider;
            context.Items[ItemKey] = site.Name;
            await next(context);
        });
}
