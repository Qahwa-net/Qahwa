namespace Qahwa.Core;

/// <summary>
/// Role Profiles and Module Profiles. A Role Profile is a named set of roles: choosing one on a User makes the user's roles
/// exactly the profile's. A Module Profile hides modules: DocTypes of a blocked module are out of reach for the user.
/// </summary>
public sealed class ProfileService(IMetaRegistry registry, IDocumentRepository repository, TimeProvider clock)
{
    private const int MaxRows = 1000;

    private bool TryMeta(string name, out DocTypeMeta meta) => registry.TryGet(name, out meta!);

    /// <summary>The modules this user may not use. Administrators and anonymous users have none.</summary>
    public async Task<IReadOnlySet<string>> BlockedModulesAsync(IUserContext user, CancellationToken ct = default)
    {
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!user.IsAuthenticated || user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal)
            || !TryMeta("User", out var userMeta) || !TryMeta("Module Profile", out var profileMeta) || !TryMeta("Block Module", out var rowMeta))
        {
            return none;
        }

        var profile = (await repository.GetAsync(userMeta, user.User, ct))?.GetValueOrDefault("module_profile") as string;
        if (string.IsNullOrEmpty(profile))
            return none;

        var rows = await repository.GetChildrenAsync(rowMeta, profileMeta.Name, profile, "blocked_modules", ct);
        return rows.Select(r => (r.GetValueOrDefault("module") as string)?.Trim())
            .Where(m => !string.IsNullOrEmpty(m)).ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    /// <summary>Called after a document is saved: applies Role Profiles to the users they concern.</summary>
    public async Task AfterSaveAsync(DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, IReadOnlySet<string> changed, bool isNew, CancellationToken ct = default)
    {
        var name = doc.GetValueOrDefault("name") as string;
        if (name is null)
            return;

        if (meta.Name == "User" && (isNew || changed.Contains("role_profile")))
        {
            if (doc.GetValueOrDefault("role_profile") is string { Length: > 0 } profile)
                await ApplyRoleProfileAsync(name, profile, ct);
        }
        else if (meta.Name == "Role Profile" && TryMeta("User", out var userMeta))
        {
            // The profile's roles may have changed: bring every user who has it up to date.
            var users = await repository.ListAsync(userMeta, new ListQuery
            {
                Filters = [new QueryFilter("role_profile", FilterOp.Eq, name)],
                Fields = ["name"],
                OrderBy = "name",
                Descending = false,
                Limit = MaxRows,
            }, ct);
            foreach (var user in users)
                await ApplyRoleProfileAsync((string)user["name"]!, name, ct);
        }
    }

    /// <summary>Makes the user's roles exactly those of the profile: missing ones are added, others are removed.</summary>
    public async Task ApplyRoleProfileAsync(string user, string profile, CancellationToken ct = default)
    {
        if (!TryMeta("Role Profile", out var profileMeta) || !TryMeta("Role Profile Role", out var rowMeta) || !TryMeta("Has Role", out var hasRole))
            return;

        var wanted = (await repository.GetChildrenAsync(rowMeta, profileMeta.Name, profile, "roles", ct))
            .Select(r => r.GetValueOrDefault("role") as string).Where(r => !string.IsNullOrEmpty(r)).ToHashSet(StringComparer.Ordinal)!;
        var current = await repository.ListAsync(hasRole, new ListQuery
        {
            Filters = [new QueryFilter("user", FilterOp.Eq, user)],
            OrderBy = "name",
            Descending = false,
            Limit = MaxRows,
        }, ct);

        var have = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in current)
        {
            var role = row.GetValueOrDefault("role") as string;
            if (role is not null && wanted.Contains(role) && have.Add(role))
                continue;
            await repository.DeleteAsync(hasRole, (string)row["name"]!, null, ct);
        }

        var utc = clock.GetUtcNow().UtcDateTime;
        var now = new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc); // PostgreSQL keeps microseconds
        foreach (var role in wanted.Where(r => !have.Contains(r!)))
        {
            await repository.InsertAsync(hasRole, new Dictionary<string, object?>
            {
                ["name"] = Guid.NewGuid().ToString("N")[..10],
                ["user"] = user,
                ["role"] = role,
                ["creation"] = now,
                ["modified"] = now,
                ["modified_by"] = UserContext.AdministratorRole,
                ["owner"] = UserContext.AdministratorRole,
                ["docstatus"] = 0,
                ["idx"] = 0,
            }, null, ct);
        }
    }
}
