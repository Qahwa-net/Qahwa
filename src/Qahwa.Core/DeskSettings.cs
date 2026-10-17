using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record DeskLink(string Id, string Label, string Path, string Group, bool DefaultVisible = true);
public sealed record DeskWorkspace(string Id, string Title);
public sealed record DeskLayout
{
    public IReadOnlyList<string> Pinned { get; init; } = [];
    public IReadOnlyList<string> Hidden { get; init; } = [];
    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyList<string> GroupOrder { get; init; } = [];
    public IReadOnlyList<string> CollapsedGroups { get; init; } = [];
    public IReadOnlyList<string> HomeShortcuts { get; init; } = [];
    public string? DefaultWorkspace { get; init; }
}
public sealed record DeskState(DeskLayout Layout, bool Personal, string? DefaultRole, DateTime? Modified,
    IReadOnlyList<DeskLink> Links, IReadOnlyList<DeskWorkspace> Workspaces);
public sealed record DeskSaveRequest(DeskLayout Layout, DateTime? Modified = null);
public sealed record DeskRoleLayout(string Role, DeskLayout Layout, DateTime? Modified);

/// <summary>Navigation preferences belong to the caller. Stored links are identifiers, never executable HTML or arbitrary URLs.</summary>
public sealed class DeskSettingsService(IMetaRegistry registry, IDocumentRepository repository, DocumentService documents,
    WorkspaceService workspaces, DashboardService dashboards, ReportService reports, HistoryRecorder history, PermissionService permissions)
{
    public const string DocTypeName = "Desk Setting";
    public const int MaxItems = 1000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private DocTypeMeta Meta => registry.Get(DocTypeName);
    private static string Key(string scope, string target) => scope + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target))).ToLowerInvariant();
    private static void RequireUser(IUserContext user) { if (!user.IsAuthenticated) throw new UnauthenticatedException(); }
    private void RequireManager(IUserContext user)
    {
        RequireUser(user);
        if (!permissions.IsAdministrator(user) && !user.Roles.Contains("System Manager", StringComparer.Ordinal))
            throw new PermissionDeniedException("Only a System Manager can set role defaults.");
    }
    private static DeskLayout Parse(Dictionary<string, object?>? row)
    {
        try { return row?.GetValueOrDefault("layout") is string json ? JsonSerializer.Deserialize<DeskLayout>(json, Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }
    private Task<Dictionary<string, object?>?> Find(string scope, string target, CancellationToken ct) => repository.GetAsync(Meta, Key(scope, target), ct);

    private async Task<List<DeskLink>> LinksAsync(IUserContext user, CancellationToken ct)
    {
        var manager = permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);
        var links = new List<DeskLink> { new("page:home", "Home", "/", "My work"), new("page:dashboards", "Dashboards", "/dashboards", "My work"), new("page:reports", "Reports", "/reports", "My work"), new("page:script-reports", "Script Reports", "/script-reports", "My work"), new("page:auto-repeat", "Auto Repeat", "/auto-repeat", "My work"), new("page:account", "My account", "/account", "My work", false) };
        if (manager)
        {
            foreach (var (path, label) in new (string, string)[] { ("designer", "DocType Designer"), ("customize", "Customize Form"), ("permissions", "Role Permissions"), ("workflows", "Workflow Builder"), ("webhooks", "Webhooks"), ("emailed-reports", "Emailed Reports"), ("bundle", "Export & Import"), ("workspaces", "Workspaces"), ("brand", "Brand"), ("print-formats", "Print Formats"), ("notification-rules", "Notification Rules"), ("assignment-rules", "Assignment Rules"), ("naming", "Naming Series") })
                links.Add(new("page:" + path, label, "/" + path, "Tools"));
            if (permissions.IsAdministrator(user)) links.Add(new("page:backup", "Backup & Restore", "/backup", "Tools"));
        }
        var metas = await documents.ListMetasAsync(user, ct);
        foreach (var meta in metas.Where(m => m.Name != DocTypeName))
        {
            var group = manager && meta.Name is "Email Account" or "Scheduled Job Log" or "Error Log" or "Deleted Document" ? "Tools" : meta.Module ?? "Other";
            links.Add(new("doctype:" + meta.Name, meta.Name, "/app/" + Uri.EscapeDataString(meta.Name), group));
        }
        foreach (var dashboard in await dashboards.ListAsync(user, ct)) links.Add(new("dashboard:" + dashboard.Id, dashboard.Title, "/dashboards/" + Uri.EscapeDataString(dashboard.Id), "Dashboards", false));
        foreach (var report in await reports.ListAsync(user, ct))
            if (metas.Any(m => m.Name == report.DocType)) links.Add(new("report:" + report.Id, report.Title, "/reports/" + Uri.EscapeDataString(report.Id), "Reports", false));
        var allowed = metas.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var filters = await repository.ListAsync(registry.Get("Saved Filter"), new ListQuery { Limit = 1000, Filters = [new("owner", FilterOp.Eq, user.User)] }, ct);
        foreach (var filter in filters)
        {
            if (filter.GetValueOrDefault("reference_doctype") is not string dt || !allowed.Contains(dt) || filter.GetValueOrDefault("filters") is not string json) continue;
            links.Add(new("filter:" + filter["name"], (filter.GetValueOrDefault("filter_name") as string ?? "Saved view") + " · " + dt,
                "/app/" + Uri.EscapeDataString(dt) + "?f=" + Uri.EscapeDataString(json), "Saved views", false));
        }
        return links;
    }

    private static DeskLayout Clean(DeskLayout layout, IReadOnlyList<DeskLink> links, IReadOnlyList<DeskWorkspace> spaces, bool strict)
    {
        var ids = links.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var groups = links.Select(x => x.Group).Append("Favorites").ToHashSet(StringComparer.Ordinal);
        IReadOnlyList<string> Check(IReadOnlyList<string>? input, HashSet<string> allowed)
        {
            if ((input?.Count ?? 0) > MaxItems || (input ?? []).Any(x => string.IsNullOrWhiteSpace(x) || x.Length > 250))
                throw new DocumentValidationException("Desk settings contain too many items or invalid identifiers.");
            if (strict && (input ?? []).Any(x => !allowed.Contains(x))) throw new DocumentValidationException("A selected Desk item is no longer available to you. Reload and try again.");
            return (input ?? []).Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToArray();
        }
        var workspace = spaces.Any(w => w.Id == layout.DefaultWorkspace) ? layout.DefaultWorkspace : null;
        if (strict && !string.IsNullOrEmpty(layout.DefaultWorkspace) && workspace is null) throw new DocumentValidationException("Choose a workspace you can access.");
        return new DeskLayout { Pinned = Check(layout.Pinned, ids), Hidden = Check(layout.Hidden, ids), Order = Check(layout.Order, ids), GroupOrder = Check(layout.GroupOrder, groups), CollapsedGroups = Check(layout.CollapsedGroups, groups), HomeShortcuts = Check(layout.HomeShortcuts, ids), DefaultWorkspace = workspace };
    }

    public async Task<DeskState> GetAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireUser(user);
        var links = await LinksAsync(user, ct);
        var spaces = (await workspaces.MineAsync(user, ct)).Select(w => new DeskWorkspace(w.Id, w.Title)).ToArray();
        var personal = await Find("user", user.User, ct);
        Dictionary<string, object?>? defaults = null;
        string? defaultRole = null;
        // Stable precedence: role name in ordinal order. Personal settings replace the selected role's layout.
        foreach (var role in user.Roles.Order(StringComparer.Ordinal))
            if (await Find("role", role, ct) is { } found) { defaults = found; defaultRole = role; break; }
        return new(Clean(Parse(personal ?? defaults), links, spaces, false), personal is not null, defaultRole, personal?.GetValueOrDefault("modified") as DateTime?, links, spaces);
    }

    private async Task StoreAsync(string scope, string target, DeskLayout layout, DateTime? modified, IUserContext actor, CancellationToken ct)
    {
        var existing = await Find(scope, target, ct);
        if (existing is not null && !Equals(existing.GetValueOrDefault("modified"), modified)) throw new ConflictException("Desk settings changed elsewhere. Reload before saving.");
        var now = history.Now();
        if (existing is null)
        {
            if (modified is not null) throw new ConflictException("Desk settings were reset elsewhere. Reload before saving.");
            var row = HistoryRecorder.Standard(actor.User, now); row["name"] = Key(scope, target); row["scope"] = scope; row["target"] = target; row["layout"] = JsonSerializer.Serialize(layout, Json);
            await repository.InsertAsync(Meta, row, null, ct);
        }
        else if (!await repository.UpdateAsync(Meta, (string)existing["name"]!, new Dictionary<string, object?> { ["layout"] = JsonSerializer.Serialize(layout, Json), ["modified"] = now, ["modified_by"] = actor.User }, modified, null, ct))
            throw new ConflictException("Desk settings changed elsewhere. Reload before saving.");
    }
    public async Task<DeskState> SaveAsync(DeskSaveRequest request, IUserContext user, CancellationToken ct = default)
    {
        if (request.Layout is null) throw new DocumentValidationException("A Desk layout is required.");
        var state = await GetAsync(user, ct);
        await StoreAsync("user", user.User, Clean(request.Layout, state.Links, state.Workspaces, true), request.Modified, user, ct);
        return await GetAsync(user, ct);
    }
    public async Task<DeskState> ResetAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireUser(user);
        if (await Find("user", user.User, ct) is { } row) await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
        return await GetAsync(user, ct);
    }
    public async Task<DeskRoleLayout> RoleAsync(string role, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        if (!await repository.ExistsAsync(registry.Get("Role"), role, ct)) throw new NotFoundException("Role not found.");
        var row = await Find("role", role, ct);
        return new(role, Parse(row), row?.GetValueOrDefault("modified") as DateTime?);
    }
    public async Task<DeskRoleLayout> SaveRoleAsync(string role, DeskSaveRequest request, IUserContext user, CancellationToken ct = default)
    {
        await RoleAsync(role, user, ct);
        if (request.Layout is null) throw new DocumentValidationException("A Desk layout is required.");
        var state = await GetAsync(user, ct);
        await StoreAsync("role", role, Clean(request.Layout, state.Links, state.Workspaces, true), request.Modified, user, ct);
        return await RoleAsync(role, user, ct);
    }
    public async Task ResetRoleAsync(string role, IUserContext user, CancellationToken ct = default)
    {
        await RoleAsync(role, user, ct);
        if (await Find("role", role, ct) is { } row) await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
    }
}
