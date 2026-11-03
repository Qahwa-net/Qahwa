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
    /// <summary>What requests see: swapped as a whole, so a request never meets a half-changed set of sites.</summary>
    private sealed record Snapshot(IReadOnlyDictionary<string, RunningSite> Sites, SiteResolver Resolver);

    private readonly SemaphoreSlim _changing = new(1, 1);
    private readonly Dictionary<string, List<BackgroundService>> _schedulers = new(StringComparer.Ordinal);
    private readonly IConfiguration _shared;
    private readonly IHostEnvironment _env;
    private readonly ILoggerFactory _loggers;
    private readonly List<ISiteServicesCustomizer> _customizers;
    private Snapshot _snapshot;
    private bool _started;

    public SiteHost(IReadOnlyList<SiteDefinition> definitions, IConfiguration shared, IHostEnvironment env, ILoggerFactory loggers, IEnumerable<ISiteServicesCustomizer> customizers, bool mainSiteServes)
    {
        _shared = shared;
        _env = env;
        _loggers = loggers;
        _customizers = customizers.ToList();
        MainSiteServes = mainSiteServes;
        var sites = definitions.ToDictionary(d => d.Name, Build, StringComparer.Ordinal);
        _snapshot = new Snapshot(sites, new SiteResolver(definitions));
    }

    private RunningSite Build(SiteDefinition definition)
    {
        var config = SiteConfiguration(definition, _shared, _env);
        var services = new ServiceCollection();
        services.AddSingleton(_loggers);
        services.AddLogging();
        services.AddSingleton(config);
        services.AddSingleton(_env);
        services.AddQahwaSite(config, _env, definition.Name);
        foreach (var customizer in _customizers)
            customizer.Customize(definition.Name, services);
        return new RunningSite(definition, services.BuildServiceProvider());
    }

    public IReadOnlyCollection<RunningSite> Sites => _snapshot.Sites.Values.ToList();

    /// <summary>True when the main site (the host's own database) answers hosts that no extra site claims.</summary>
    public bool MainSiteServes { get; }

    public bool IsEmpty => _snapshot.Resolver.IsEmpty;

    public RunningSite? Resolve(string? host)
    {
        var snapshot = _snapshot;
        return snapshot.Resolver.Resolve(host) is { } definition ? snapshot.Sites[definition.Name] : null;
    }

    /// <summary>
    /// Starts a site while the server runs: checked against the running sites first (names, host names and databases must not
    /// clash), brought up to date, its schedulers started, and only then does it begin to answer requests. A failure leaves the
    /// running sites exactly as they were.
    /// </summary>
    /// <exception cref="SiteConfigException">The site clashes with a running one.</exception>
    public async Task<RunningSite> AddAsync(SiteDefinition definition, bool autoMigrate, CancellationToken ct = default)
    {
        await _changing.WaitAsync(ct);
        try
        {
            var current = _snapshot;
            var problems = SiteCatalog.Check([.. current.Sites.Values.Select(s => s.Definition), definition]).ToList();
            if (current.Sites.ContainsKey(definition.Name))
                problems.Add($"The site '{definition.Name}' is already running.");
            if (problems.Count > 0)
                throw new SiteConfigException(problems);

            var site = Build(definition);
            try
            {
                await SiteStartup.RunAsync(site.Provider, _loggers.CreateLogger($"Qahwa.Site.{definition.Name}"), autoMigrate);
                if (_started)
                    await StartSchedulersAsync(site, ct);
            }
            catch
            {
                await StopSchedulersAsync(definition.Name, CancellationToken.None);
                await site.Provider.DisposeAsync();
                throw;
            }

            var sites = new Dictionary<string, RunningSite>(current.Sites, StringComparer.Ordinal) { [definition.Name] = site };
            _snapshot = new Snapshot(sites, new SiteResolver(sites.Values.Select(s => s.Definition)));
            return site;
        }
        finally
        {
            _changing.Release();
        }
    }

    /// <summary>Stops serving a site and shuts its schedulers and services down. Its database and files are left alone.</summary>
    /// <returns>False when no such site is running.</returns>
    public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
    {
        await _changing.WaitAsync(ct);
        try
        {
            var current = _snapshot;
            if (!current.Sites.TryGetValue(name, out var site))
                return false;
            var sites = current.Sites.Where(s => s.Key != name).ToDictionary(s => s.Key, s => s.Value, StringComparer.Ordinal);
            _snapshot = new Snapshot(sites, new SiteResolver(sites.Values.Select(s => s.Definition))); // no new request reaches it from here
            await StopSchedulersAsync(name, ct);
            await site.Provider.DisposeAsync();
            return true;
        }
        finally
        {
            _changing.Release();
        }
    }

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
        foreach (var site in _snapshot.Sites.Values)
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
        await _changing.WaitAsync(cancellationToken);
        try
        {
            foreach (var site in _snapshot.Sites.Values)
                await StartSchedulersAsync(site, cancellationToken);
            _started = true;
        }
        finally
        {
            _changing.Release();
        }
    }

    private async Task StartSchedulersAsync(RunningSite site, CancellationToken ct)
    {
        var list = _schedulers[site.Name] = [];
        var scheduler = ActivatorUtilities.CreateInstance<SchedulerService>(site.Provider);
        list.Add(scheduler);
        await scheduler.StartAsync(ct);
        var backplane = ActivatorUtilities.CreateInstance<RealtimeBackplaneService>(site.Provider);
        list.Add(backplane);
        await backplane.StartAsync(ct);
    }

    private async Task StopSchedulersAsync(string name, CancellationToken ct)
    {
        if (!_schedulers.Remove(name, out var list))
            return;
        foreach (var scheduler in list)
        {
            await scheduler.StopAsync(ct);
            scheduler.Dispose();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var scheduler in _schedulers.Values.SelectMany(l => l))
            await scheduler.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var scheduler in _schedulers.Values.SelectMany(l => l))
            scheduler.Dispose();
        foreach (var site in _snapshot.Sites.Values)
            await site.Provider.DisposeAsync();
        _changing.Dispose();
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
