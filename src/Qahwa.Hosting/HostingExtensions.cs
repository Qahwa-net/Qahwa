using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Core.Designer;
using Qahwa.Data;

namespace Qahwa.Hosting;

public sealed class QahwaOptions
{
    /// <summary>Absolute paths of directories containing DocType JSON files (searched recursively).</summary>
    public List<string> DocTypePaths { get; } = [];
}

public sealed record MigrationResult(IReadOnlyList<SchemaPlan> Plans, bool Applied);

public sealed class QahwaMigrator(IMetaRegistry registry, SchemaSync sync, CustomDocTypeLoader customLoader, CustomizationManager customization)
{
    /// <summary>Plans (and unless <paramref name="planOnly"/>, applies) schema changes for every DocType.</summary>
    public async Task<MigrationResult> MigrateAsync(bool planOnly = false, CancellationToken ct = default)
    {
        if (!planOnly)
            await sync.EnsureSystemTablesAsync(ct);

        // DocTypes created in the designer are part of the schema too.
        await customLoader.LoadAsync(ct);

        // So are the fields added to standard DocTypes in the Customize Form.
        await customization.LoadAsync(ct);

        var plans = new List<SchemaPlan>();
        foreach (var meta in registry.All.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var plan = await sync.PlanAsync(meta, ct);
            if (!planOnly)
                await sync.ApplyAsync(plan, ct);
            plans.Add(plan);
        }

        return new MigrationResult(plans, !planOnly);
    }
}

public static class HostingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the framework core. The host must also register a database provider
    /// (for example <c>AddQahwaPostgres</c>) and configure <see cref="QahwaOptions"/> and <see cref="AuthOptions"/>.
    /// </summary>
    public static IServiceCollection AddQahwa(this IServiceCollection services)
    {
        services.AddOptions<QahwaOptions>();
        services.AddOptions<AuthOptions>();
        services.TryAddSingleton<MetaRegistry>(sp =>
        {
            var registry = new MetaRegistry();
            registry.AddCoreDocTypes();
            foreach (var path in sp.GetRequiredService<IOptions<QahwaOptions>>().Value.DocTypePaths)
                registry.LoadDirectory(path);
            foreach (var app in sp.GetServices<IQahwaApp>().Where(a => a.DocTypesPath is not null))
                registry.LoadDirectory(app.DocTypesPath!);
            registry.ValidateReferences();
            return registry;
        });
        services.TryAddSingleton<IMetaRegistry>(sp => sp.GetRequiredService<MetaRegistry>());
        services.TryAddSingleton<IMutableMetaRegistry>(sp => sp.GetRequiredService<MetaRegistry>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher<string>>(sp => new PasswordHasher<string>(Options.Create(
            new PasswordHasherOptions { IterationCount = sp.GetRequiredService<IOptions<AuthOptions>>().Value.HashIterations })));
        services.TryAddSingleton<PermissionOverrides>();
        services.TryAddSingleton<IPermissionOverrides>(sp => sp.GetRequiredService<PermissionOverrides>());
        services.TryAddSingleton<IPermissionStore, PermissionStore>();
        services.TryAddSingleton(sp => new PermissionService(sp.GetRequiredService<IPermissionOverrides>()));
        services.TryAddSingleton<PermissionManager>();
        services.TryAddSingleton<ICustomizationStore, CustomizationStore>();
        services.TryAddSingleton<CustomizationManager>();
        services.TryAddSingleton<IWorkflowStore, WorkflowStore>();
        services.TryAddSingleton<WorkflowCatalog>();
        services.TryAddSingleton<WorkflowManager>();
        services.TryAddSingleton<IAssignmentRuleStore, AssignmentRuleStore>();
        services.TryAddSingleton<EmailService>();
        services.TryAddSingleton<DashboardService>();
        services.TryAddSingleton<PrintService>();
        services.TryAddSingleton<WorkspaceService>();
        services.TryAddSingleton<DataImportService>();
        services.TryAddSingleton<ApiKeyService>();
        services.TryAddSingleton(sp => new FileService(sp.GetRequiredService<IMetaRegistry>(), sp.GetRequiredService<IDocumentRepository>(), sp.GetRequiredService<DocumentService>(),
            sp.GetRequiredService<PermissionService>(), sp.GetRequiredService<NotificationService>(), sp.GetRequiredService<IFileStorage>(), sp.GetRequiredService<HistoryRecorder>())
        { MaxBytes = (sp.GetService<AttachmentOptions>()?.MaxMegabytes ?? 25) * 1024L * 1024L });
        services.TryAddSingleton(sp => new Lazy<FileService>(() => sp.GetRequiredService<FileService>()));
        services.TryAddSingleton<IWebhookStore, WebhookStore>();
        services.TryAddSingleton<WebhookCatalog>();
        services.TryAddSingleton(sp => new WebhookManager(sp.GetRequiredService<IMetaRegistry>(), sp.GetRequiredService<IWebhookStore>(), sp.GetRequiredService<WebhookCatalog>(),
            sp.GetRequiredService<PermissionService>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<WebhookOptions>()?.AllowInsecure ?? false));
        services.TryAddSingleton<WebhookService>();
        services.TryAddSingleton(sp => new Lazy<WebhookService>(() => sp.GetRequiredService<WebhookService>()));
        services.TryAddSingleton<AssignmentRuleCatalog>();
        services.TryAddSingleton<AssignmentRuleManager>();
        services.TryAddSingleton<AssignmentRuleEngine>();
        services.TryAddSingleton<INotificationRuleStore, NotificationRuleStore>();
        services.TryAddSingleton<NotificationRuleCatalog>();
        services.TryAddSingleton<NotificationRuleManager>();
        services.TryAddSingleton<NotificationRuleEngine>();
        services.TryAddSingleton(sp => new Lazy<NotificationRuleEngine>(() => sp.GetRequiredService<NotificationRuleEngine>()));
        services.TryAddSingleton(sp => new Lazy<AssignmentRuleEngine>(() => sp.GetRequiredService<AssignmentRuleEngine>()));
        services.TryAddSingleton<NamingService>();
        services.TryAddSingleton<UserPermissionService>();
        services.TryAddSingleton<ProfileService>();
        services.TryAddSingleton<HistoryRecorder>();
        services.TryAddSingleton(sp => new Lazy<DocumentService>(() => sp.GetRequiredService<DocumentService>()));
        services.TryAddSingleton<NotificationService>();
        services.TryAddSingleton<RealtimeHub>();
        services.TryAddSingleton<IRealtimePublisher>(sp => sp.GetRequiredService<RealtimeHub>());
        services.TryAddSingleton<FollowService>();
        services.TryAddSingleton<LabelService>();
        services.TryAddSingleton<CollaborationHooks>();
        services.TryAddSingleton<AssignmentService>();
        services.TryAddSingleton<AutoRepeatService>();
        services.TryAddSingleton<AutoEmailReportService>();
        services.TryAddSingleton<EmailDigestService>();
        services.TryAddSingleton<JobLogService>();
        services.TryAddSingleton<ListSettingsService>();
        services.TryAddSingleton<BulkService>();
        services.TryAddSingleton<ActivityService>();
        services.TryAddSingleton<NamingSeriesManager>();
        services.TryAddSingleton<ShareStore>();
        services.TryAddSingleton<ShareManager>();
        services.TryAddSingleton<DocumentService>();
        services.TryAddSingleton<AuthService>();
        services.TryAddSingleton<SqlBuilder>();
        services.TryAddSingleton<IDocumentRepository, DocumentRepository>();
        services.TryAddSingleton<IAuthStore, AuthStore>();
        services.TryAddSingleton<ITotpStore, TotpStore>();
        services.TryAddSingleton<ISessionStore, SessionStore>();
        services.TryAddSingleton<SessionService>();
        services.TryAddSingleton<IIdentityStore, IdentityStore>();
        services.TryAddSingleton<SsoService>();
        services.TryAddSingleton<LdapService>();
        services.TryAddSingleton<SamlService>();
        services.TryAddSingleton<PasswordResetService>();
        services.TryAddSingleton<SignUpService>();
        services.TryAddSingleton<SystemSettingsService>();
        services.TryAddSingleton<PatchRunner>();
        services.TryAddSingleton<IPdfRenderer, NoPdfRenderer>();
        services.TryAddSingleton<PdfService>();
        services.AddSingleton<IPatch, MasterDataPatch>();
        services.TryAddSingleton<ReportService>();
        services.TryAddSingleton<BackupService>();
        services.TryAddSingleton<DeletedDocumentService>();
        services.TryAddSingleton(sp =>
        {
            try
            {
                return IpAllowList.Parse(sp.GetRequiredService<IOptions<AuthOptions>>().Value.AllowedIps);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Qahwa:Auth:AllowedIps is not valid: " + ex.Message, ex);
            }
        });
        services.TryAddSingleton<TwoFactorService>();
        services.TryAddSingleton<SchemaSync>();
        services.TryAddSingleton<ISchemaManager, SchemaManager>();
        services.TryAddSingleton<IDocTypeStore, DocTypeStore>();
        services.TryAddSingleton(sp => new DeveloperExport(sp.GetService<IOptions<DeveloperOptions>>()?.Value ?? new DeveloperOptions()));
        services.TryAddSingleton<IDeveloperExport>(sp => sp.GetRequiredService<DeveloperExport>());
        services.TryAddSingleton<DocTypeDesigner>();
        services.TryAddSingleton<BundleService>();
        services.TryAddSingleton<TranslationService>();
        services.TryAddSingleton<HitCounter>();
        services.TryAddSingleton<ErrorLogService>();
        services.TryAddSingleton<GlobalSearchService>();
        services.TryAddSingleton<AppCatalog>();
        services.TryAddSingleton<AppHookRunner>();
        services.TryAddSingleton(sp => new Lazy<AppHookRunner>(() => sp.GetRequiredService<AppHookRunner>()));
        services.TryAddSingleton<MethodService>();
        services.TryAddSingleton<AppInstaller>();
        services.TryAddSingleton<AppJobRunner>();
        services.TryAddSingleton<DocumentActionEngine>();
        services.TryAddSingleton(sp => new Lazy<DocumentActionEngine>(() => sp.GetRequiredService<DocumentActionEngine>()));
        services.TryAddSingleton<ClientScriptService>();
        services.TryAddSingleton<ScriptSandbox>();
        services.TryAddSingleton<ServerScriptEngine>();
        services.TryAddSingleton(sp => new Lazy<ServerScriptEngine>(() => sp.GetRequiredService<ServerScriptEngine>()));
        services.TryAddSingleton<WebFormService>();
        services.TryAddSingleton<CustomDocTypeLoader>();
        services.TryAddSingleton<QahwaMigrator>();
        return services;
    }
}

public static class PatchRegistration
{
    /// <summary>Registers a one-time data patch (see <see cref="IPatch"/>); it runs at the next start-up on every site that has not run it.</summary>
    public static IServiceCollection AddQahwaPatch<T>(this IServiceCollection services) where T : class, IPatch =>
        services.AddSingleton<IPatch, T>();
}
