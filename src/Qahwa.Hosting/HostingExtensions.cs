using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Qahwa.Core;
using Qahwa.Core.Auth;
using Qahwa.Data;

namespace Qahwa.Hosting;

public sealed class QahwaOptions
{
    /// <summary>Absolute paths of directories containing DocType JSON files (searched recursively).</summary>
    public List<string> DocTypePaths { get; } = [];
}

public sealed record MigrationResult(IReadOnlyList<SchemaPlan> Plans, bool Applied);

public sealed class QahwaMigrator(IMetaRegistry registry, SchemaSync sync)
{
    /// <summary>Plans (and unless <paramref name="planOnly"/>, applies) schema changes for every DocType.</summary>
    public async Task<MigrationResult> MigrateAsync(bool planOnly = false, CancellationToken ct = default)
    {
        if (!planOnly)
            await sync.EnsureSystemTablesAsync(ct);

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
        services.TryAddSingleton<IMetaRegistry>(sp =>
        {
            var registry = new MetaRegistry();
            registry.AddCoreDocTypes();
            foreach (var path in sp.GetRequiredService<IOptions<QahwaOptions>>().Value.DocTypePaths)
                registry.LoadDirectory(path);
            registry.ValidateReferences();
            return registry;
        });

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IPasswordHasher<string>>(sp => new PasswordHasher<string>(Options.Create(
            new PasswordHasherOptions { IterationCount = sp.GetRequiredService<IOptions<AuthOptions>>().Value.HashIterations })));
        services.TryAddSingleton<PermissionService>();
        services.TryAddSingleton<NamingService>();
        services.TryAddSingleton<DocumentService>();
        services.TryAddSingleton<AuthService>();
        services.TryAddSingleton<SqlBuilder>();
        services.TryAddSingleton<IDocumentRepository, DocumentRepository>();
        services.TryAddSingleton<IAuthStore, AuthStore>();
        services.TryAddSingleton<SchemaSync>();
        services.TryAddSingleton<QahwaMigrator>();
        return services;
    }
}
