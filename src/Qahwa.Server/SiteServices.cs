using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Qahwa.Api;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
using Qahwa.Data.Postgres;
using Qahwa.Hosting;

/// <summary>Lets a host (or a test) change what one site gets, for example to give it other stores.</summary>
public interface ISiteServicesCustomizer
{
    void Customize(string siteName, IServiceCollection services);
}

public static class QahwaSiteServices
{
    /// <summary>
    /// Everything one site needs: the framework, its database, the API and the settings read from <paramref name="config"/>. The same
    /// function serves the main (default) site and every extra site, each with its own container, so nothing (caches, stores, rate
    /// counters, secrets) can leak from one site to another.
    /// </summary>
    public static void AddQahwaSite(this IServiceCollection services, IConfiguration config, IHostEnvironment env, string? siteName)
    {
        services.AddOptions<QahwaOptions>().Configure(options =>
        {
            foreach (var path in config.GetSection("Qahwa:DocTypePaths").Get<string[]>() ?? [])
                options.DocTypePaths.Add(Path.GetFullPath(path, env.ContentRootPath));
        });
        services.AddOptions<AuthOptions>().Bind(config.GetSection("Qahwa:Auth"));

        services.AddQahwa();
        services.AddQahwaPostgres(_ => config.GetConnectionString("Default") ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured."));
        services.AddQahwaApi();
        if (siteName is not null)
            services.AddDataProtection().SetApplicationName("qahwa:site:" + siteName); // a token made for one site means nothing at another

        var email = config.GetSection("Qahwa:Email").Get<EmailSettings>() ?? new EmailSettings();
        services.AddSingleton(email);
        if (string.IsNullOrWhiteSpace(email.Host))
            services.AddSingleton<IEmailSender, LogEmailSender>();
        else
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddSingleton(config.GetSection("Qahwa:Webhooks").Get<WebhookOptions>() ?? new WebhookOptions());
        services.AddSingleton<IWebhookSender, WebhookSender>();
        var files = config.GetSection("Qahwa:Files").Get<AttachmentOptions>() ?? new AttachmentOptions();
        files.MaxMegabytes = Math.Clamp(files.MaxMegabytes, 1, 100);
        services.AddSingleton(files);
        services.AddSingleton<IFileStorage>(new DiskFileStorage(config["Qahwa:Files:Path"] ?? Path.Combine(env.ContentRootPath, "data", "files")));
        services.Configure<SsoOptions>(config.GetSection("Qahwa:Auth:Sso"));
        services.Configure<SignUpOptions>(config.GetSection("Qahwa:Auth:SignUp"));
        services.Configure<LdapOptions>(config.GetSection("Qahwa:Auth:Ldap"));
        services.AddSingleton(config.GetSection("Qahwa:Backup").Get<BackupOptions>() ?? new BackupOptions());
        services.Replace(ServiceDescriptor.Singleton<IPdfRenderer>(new BrowserPdfRenderer(config.GetSection("Qahwa:Pdf").Get<PdfOptions>() ?? new PdfOptions())));
    }
}

/// <summary>What happens when a site starts: its schema, its loaded definitions and its pending patches.</summary>
public static class SiteStartup
{
    /// <summary>Brings one site's database and in-memory definitions up to date. A problem stops the server: carrying on half-migrated is worse.</summary>
    public static async Task RunAsync(IServiceProvider sp, ILogger logger, bool autoMigrate)
    {
        if (autoMigrate)
        {
            var result = await sp.GetRequiredService<QahwaMigrator>().MigrateAsync();
            foreach (var plan in result.Plans)
                foreach (var warning in plan.Warnings)
                    logger.LogWarning("Schema: {Warning}", warning);
        }

        // DocTypes created in the designer live in the database; load them (a no-op if already loaded by the migration).
        foreach (var failure in await sp.GetRequiredService<CustomDocTypeLoader>().LoadAsync())
            logger.LogError("{Failure}", failure);

        // Fields and properties customised in the Customize Form (a no-op if the migration already applied them).
        foreach (var failure in await sp.GetRequiredService<CustomizationManager>().LoadAsync())
            logger.LogError("{Failure}", failure);

        // A mistake in Qahwa:Auth:AllowedIps or Qahwa:Auth:Ldap must stop the server here, not quietly open or close the system.
        _ = sp.GetRequiredService<IpAllowList>();
        if (LdapService.Problem(sp.GetRequiredService<IOptions<LdapOptions>>().Value) is { } ldapProblem)
            throw new InvalidOperationException(ldapProblem);

        foreach (var failure in await sp.GetRequiredService<WebhookManager>().LoadAsync())
            logger.LogError("{Failure}", failure);
        foreach (var failure in await sp.GetRequiredService<NotificationRuleManager>().LoadAsync())
            logger.LogError("{Failure}", failure);
        foreach (var failure in await sp.GetRequiredService<AssignmentRuleManager>().LoadAsync())
            logger.LogError("{Failure}", failure);
        foreach (var failure in await sp.GetRequiredService<WorkflowManager>().LoadAsync())
            logger.LogError("{Failure}", failure);

        // One-time data patches that have not run on this site yet.
        var patched = await sp.GetRequiredService<PatchRunner>().RunPendingAsync();
        foreach (var name in patched.Ran)
            logger.LogInformation("Patch {Patch} ran.", name);

        // Permission rules customised in the Role Permission Manager.
        foreach (var failure in await sp.GetRequiredService<PermissionManager>().LoadAsync())
            logger.LogError("{Failure}", failure);
    }
}
