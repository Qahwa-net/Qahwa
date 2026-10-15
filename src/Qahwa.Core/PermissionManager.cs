namespace Qahwa.Core;

/// <summary>Customised permission rules of one DocType, stored as JSON.</summary>
public sealed record StoredPermissions(string DocType, string Rules, DateTime Modified, string ModifiedBy);

public interface IPermissionStore
{
    /// <summary>Returns an empty list when the table has not been created yet.</summary>
    Task<IReadOnlyList<StoredPermissions>> ListAsync(CancellationToken ct = default);

    Task<StoredPermissions?> GetAsync(string docType, CancellationToken ct = default);

    /// <param name="expectedModified">Null when the DocType has no customised rules yet.</param>
    /// <returns>False when someone else changed (or created) the rules since <paramref name="expectedModified"/>.</returns>
    Task<bool> SaveAsync(StoredPermissions row, DateTime? expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string docType, CancellationToken ct = default);
}

public sealed record PermissionItem(string Name, string? Module, DocTypeSource Source, bool Customized, int RuleCount);

/// <summary>A field that is not at the ordinary level 0, so the permission matrix can say which fields a level protects.</summary>
public sealed record FieldLevelInfo(string Name, string? Label, int Level);

public sealed record PermissionView(
    string Name,
    DocTypeSource Source,
    bool Customized,
    DateTime? Modified,
    IReadOnlyList<DocPermMeta> Rules,
    IReadOnlyList<DocPermMeta> DefaultRules,
    IReadOnlyList<FieldLevelInfo> Fields,
    bool CanEdit,
    bool IsSubmittable = false);

/// <summary>
/// The Role Permission Manager: who may read, write, create and delete documents of any DocType, at which field
/// level, without touching the DocType's files. Customised rules replace the rules of the definition and can be reset.
/// </summary>
public sealed class PermissionManager(
    IMetaRegistry registry,
    IPermissionStore store,
    PermissionOverrides overrides,
    TimeProvider clock)
{
    private const string SystemManagerRole = "System Manager";

    public async Task<IReadOnlyList<PermissionItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await Task.CompletedTask;
        return registry.All
            .Where(m => !m.IsChildTable)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var custom = overrides.Get(m.Name);
                return new PermissionItem(
                    m.Name, m.Module, registry.SourceOf(m.Name) ?? DocTypeSource.App, custom is not null, (custom ?? m.Permissions).Count);
            })
            .ToList();
    }

    public async Task<PermissionView> GetAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = RequireManageable(name);
        var stored = await store.GetAsync(meta.Name, ct);
        var custom = overrides.Get(meta.Name);
        var source = registry.SourceOf(meta.Name) ?? DocTypeSource.App;

        return new PermissionView(
            meta.Name,
            source,
            custom is not null,
            stored?.Modified,
            custom ?? meta.Permissions,
            meta.Permissions,
            meta.Fields.Where(f => f.PermLevel > 0).Select(f => new FieldLevelInfo(f.Name, f.Label, f.PermLevel)).ToList(),
            CanEdit(source, user),
            meta.IsSubmittable);
    }

    public async Task<PermissionView> SaveAsync(
        string name,
        IReadOnlyList<DocPermMeta> rules,
        DateTime? expectedModified,
        IUserContext user,
        CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = RequireManageable(name);
        RequireEditable(registry.SourceOf(meta.Name) ?? DocTypeSource.App, user);

        var cleaned = rules.Select(r => r with { Role = r.Role?.Trim() ?? string.Empty }).ToList();
        var errors = DocTypeLoader.CheckPermissionRules(cleaned, "rules");
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var now = Now();
        var row = new StoredPermissions(meta.Name, DocTypeLoader.SerializeRules(cleaned), now, user.User);
        if (!await store.SaveAsync(row, expectedModified, ct))
            throw new ConflictException($"The permissions of '{meta.Name}' were changed by someone else. Reload and try again.");

        overrides.Set(meta.Name, cleaned);
        return await GetAsync(meta.Name, user, ct);
    }

    /// <summary>Goes back to the rules in the DocType's own definition.</summary>
    public async Task<PermissionView> ResetAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = RequireManageable(name);
        RequireEditable(registry.SourceOf(meta.Name) ?? DocTypeSource.App, user);

        await store.DeleteAsync(meta.Name, ct);
        overrides.Remove(meta.Name);
        return await GetAsync(meta.Name, user, ct);
    }

    /// <summary>Loads the customised rules into memory (at startup). Returns one message per row that could not be read.</summary>
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                overrides.Set(row.DocType, DocTypeLoader.ParseRules(row.Rules));
            }
            catch (DocTypeValidationException ex)
            {
                failures.Add($"Permissions of '{row.DocType}' were skipped: {ex.Message}");
            }
        }

        return failures;
    }

    private DocTypeMeta RequireManageable(string name)
    {
        var meta = registry.Get(name);
        if (meta.IsChildTable)
            throw new NotFoundException($"DocType '{name}' was not found."); // child rows follow their parent

        return meta;
    }

    /// <summary>The framework's own DocTypes (users, roles) can only be changed by an Administrator: a System Manager could otherwise promote themselves.</summary>
    private static bool CanEdit(DocTypeSource source, IUserContext user) =>
        source != DocTypeSource.Core || user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal);

    private static void RequireEditable(DocTypeSource source, IUserContext user)
    {
        if (!CanEdit(source, user))
            throw new PermissionDeniedException("Only an Administrator can change the permissions of the framework's own DocTypes.");
    }

    private static void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();

        if (!user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal)
            && !user.Roles.Contains(SystemManagerRole, StringComparer.Ordinal))
        {
            throw new PermissionDeniedException("Only a System Manager can manage permissions.");
        }
    }

    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
