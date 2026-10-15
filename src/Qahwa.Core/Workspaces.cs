using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>A link on a workspace: to a DocType's list, a dashboard, or one of the Desk's own pages.</summary>
public sealed record WorkspaceShortcut(string Label, string Kind, string Target);

/// <summary>One block of a workspace page: a heading, a row of shortcuts, or a dashboard's tiles.</summary>
public sealed record WorkspaceBlock
{
    public const string Heading = "Heading";
    public const string Shortcuts = "Shortcuts";
    public const string Dashboard = "Dashboard";
    public static readonly string[] Types = [Heading, Shortcuts, Dashboard];

    public const string DocTypeKind = "DocType";
    public const string DashboardKind = "Dashboard";
    public const string PageKind = "Page";
    public static readonly string[] Kinds = [DocTypeKind, DashboardKind, PageKind];

    public const int MaxBlocks = 30;
    public const int MaxShortcuts = 24;

    public required string Type { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<WorkspaceShortcut> Items { get; init; } = [];
    public string? DashboardId { get; init; }
}

/// <param name="Roles">Who sees the workspace: people with any of these roles; empty means everyone signed in.</param>
public sealed record WorkspaceRequest(string? Title = null, int Sequence = 0, IReadOnlyList<string>? Roles = null, IReadOnlyList<WorkspaceBlock>? Blocks = null);

public sealed record WorkspaceInfo(string Id, string Title, int Sequence, IReadOnlyList<string> Roles, int Blocks);

public sealed record WorkspaceDetail(WorkspaceInfo Info, IReadOnlyList<WorkspaceBlock> Blocks);

/// <summary>A shortcut as shown: <see cref="Path"/> is the Desk route it leads to.</summary>
public sealed record ShownShortcut(string Label, string Kind, string Path);

/// <summary>A block as one person sees it: shortcuts they may not use are gone, and a dashboard they cannot open is gone.</summary>
public sealed record WorkspaceBlockView(string Type, string? Text = null, IReadOnlyList<ShownShortcut>? Shortcuts = null, string? DashboardId = null, string? DashboardTitle = null);

public sealed record WorkspaceView(string Id, string Title, IReadOnlyList<WorkspaceBlockView> Blocks);

/// <summary>
/// Workspaces: the pages people start from. A System Manager arranges headings, shortcut rows and dashboards into pages and
/// says which roles see each one; everybody sees the workspaces for their roles, with only the shortcuts they are allowed to use.
/// </summary>
public sealed class WorkspaceService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DashboardService dashboards,
    PermissionService permissions,
    HistoryRecorder history)
{
    public const string DocTypeName = "Workspace";
    public const int MaxWorkspaces = 30;
    public const int MaxRoles = 20;

    /// <summary>The Desk's own pages a shortcut may lead to, and whether only System Managers can use them.</summary>
    public static readonly IReadOnlyDictionary<string, bool> Pages = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["dashboards"] = false, ["auto-repeat"] = false, ["account"] = false,
        ["designer"] = true, ["customize"] = true, ["permissions"] = true, ["workflows"] = true, ["assignment-rules"] = true,
        ["notification-rules"] = true, ["print-formats"] = true, ["naming"] = true,
    };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!IsManager(user))
            throw new PermissionDeniedException("Only a System Manager can manage workspaces.");
    }

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private static IReadOnlyList<WorkspaceBlock> ParseBlocks(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? [] : JsonSerializer.Deserialize<List<WorkspaceBlock>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ParseRoles(string? text) =>
        (text ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static WorkspaceInfo ToInfo(Dictionary<string, object?> row) =>
        new((string)row["name"]!, row["title"] as string ?? string.Empty, Convert.ToInt32(row["sequence"] ?? 0), ParseRoles(row["roles"] as string), ParseBlocks(row["blocks"] as string).Count);

    private bool Sees(Dictionary<string, object?> row, IUserContext user)
    {
        var roles = ParseRoles(row["roles"] as string);
        return roles.Count == 0 || roles.Any(r => user.Roles.Contains(r, StringComparer.Ordinal)) || permissions.IsAdministrator(user);
    }

    // ---- reading (everyone) ----

    /// <summary>The workspaces for this person, in order, with their blocks filtered to what they may use.</summary>
    public async Task<IReadOnlyList<WorkspaceView>> MineAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Meta, new ListQuery { OrderBy = "sequence", Descending = false, Limit = 200 }, ct);
        var visibleDashboards = (await dashboards.ListAsync(user, ct)).ToDictionary(d => d.Id, d => d.Title, StringComparer.Ordinal);
        var readable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool CanOpen(string doctype)
        {
            if (readable.TryGetValue(doctype, out var known))
                return known;
            return readable[doctype] = registry.TryGet(doctype, out var meta) && !meta.IsChildTable && permissions.Evaluate(meta, user, PermAction.Read) != AccessLevel.None;
        }

        var views = new List<WorkspaceView>();
        foreach (var row in rows.Where(r => Sees(r, user)).OrderBy(r => Convert.ToInt32(r["sequence"] ?? 0)).ThenBy(r => r["title"] as string, StringComparer.OrdinalIgnoreCase))
        {
            var blocks = new List<WorkspaceBlockView>();
            foreach (var block in ParseBlocks(row["blocks"] as string))
            {
                switch (block.Type)
                {
                    case WorkspaceBlock.Heading:
                        blocks.Add(new WorkspaceBlockView(block.Type, Text: block.Text));
                        break;
                    case WorkspaceBlock.Dashboard:
                        if (block.DashboardId is { } id && visibleDashboards.TryGetValue(id, out var title))
                            blocks.Add(new WorkspaceBlockView(block.Type, DashboardId: id, DashboardTitle: title));
                        break;
                    default:
                    {
                        var shown = new List<ShownShortcut>();
                        foreach (var s in block.Items)
                        {
                            var path = s.Kind switch
                            {
                                WorkspaceBlock.DocTypeKind when CanOpen(s.Target) => $"/app/{Uri.EscapeDataString(s.Target)}",
                                WorkspaceBlock.DashboardKind when visibleDashboards.ContainsKey(s.Target) => $"/dashboards/{Uri.EscapeDataString(s.Target)}",
                                WorkspaceBlock.PageKind when Pages.TryGetValue(s.Target, out var managersOnly) && (!managersOnly || IsManager(user)) => $"/{s.Target}",
                                _ => null,
                            };
                            if (path is not null)
                                shown.Add(new ShownShortcut(s.Label, s.Kind, path));
                        }

                        if (shown.Count > 0)
                            blocks.Add(new WorkspaceBlockView(block.Type, Shortcuts: shown));
                        break;
                    }
                }
            }

            views.Add(new WorkspaceView((string)row["name"]!, row["title"] as string ?? string.Empty, blocks));
        }

        return views;
    }

    // ---- managing (System Managers) ----

    public async Task<IReadOnlyList<WorkspaceInfo>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var rows = await repository.ListAsync(Meta, new ListQuery { OrderBy = "sequence", Descending = false, Limit = 200 }, ct);
        return rows.Select(ToInfo).ToList();
    }

    public async Task<WorkspaceDetail> GetAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var row = await repository.GetAsync(Meta, id, ct) ?? throw new NotFoundException("Workspace not found.");
        return new WorkspaceDetail(ToInfo(row), ParseBlocks(row["blocks"] as string));
    }

    private async Task<(string Title, IReadOnlyList<string> Roles, IReadOnlyList<WorkspaceBlock> Blocks)> ValidateAsync(WorkspaceRequest request, CancellationToken ct)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length is 0 or > 100 || title.Any(char.IsControl))
            throw Invalid("title", "Give the workspace a title of up to 100 characters.");
        if (request.Sequence is < 0 or > 10_000)
            throw Invalid("sequence", "Use a position from 0 to 10000.");

        var roles = (request.Roles ?? []).Select(r => r?.Trim() ?? string.Empty).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        var errors = new List<FieldError>();
        if (roles.Count > MaxRoles)
            errors.Add(new FieldError("roles", $"At most {MaxRoles} roles."));
        if (registry.TryGet("Role", out var roleMeta))
        {
            foreach (var role in roles.Take(MaxRoles).Where(r => r.Length <= FieldTypes.ShortTextLength))
            {
                if (!await repository.ExistsAsync(roleMeta, role, ct))
                    errors.Add(new FieldError("roles", $"'{role}' is not a role."));
            }
        }

        var blocks = (request.Blocks ?? []).ToList();
        if (blocks.Count > WorkspaceBlock.MaxBlocks)
            errors.Add(new FieldError("blocks", $"At most {WorkspaceBlock.MaxBlocks} blocks."));
        var cleaned = new List<WorkspaceBlock>();
        for (var i = 0; i < Math.Min(blocks.Count, WorkspaceBlock.MaxBlocks); i++)
        {
            var b = blocks[i];
            void Add(string field, string message) => errors.Add(new FieldError($"blocks[{i}].{field}", message));
            if (!WorkspaceBlock.Types.Contains(b.Type))
            {
                Add("type", $"Use one of: {string.Join(", ", WorkspaceBlock.Types)}.");
                continue;
            }

            switch (b.Type)
            {
                case WorkspaceBlock.Heading:
                    if (string.IsNullOrWhiteSpace(b.Text) || b.Text.Trim().Length > 100 || b.Text.Any(char.IsControl))
                        Add("text", "Write a heading of up to 100 characters.");
                    cleaned.Add(new WorkspaceBlock { Type = b.Type, Text = b.Text?.Trim() });
                    break;
                case WorkspaceBlock.Dashboard:
                    if (b.DashboardId is null || !await repository.ExistsAsync(registry.Get(DashboardService.DocTypeName), b.DashboardId, ct))
                        Add("dashboardId", "Choose a dashboard.");
                    cleaned.Add(new WorkspaceBlock { Type = b.Type, DashboardId = b.DashboardId });
                    break;
                default:
                {
                    if (b.Items.Count is 0 or > WorkspaceBlock.MaxShortcuts)
                        Add("items", $"Add 1 to {WorkspaceBlock.MaxShortcuts} shortcuts.");
                    var items = new List<WorkspaceShortcut>();
                    for (var s = 0; s < Math.Min(b.Items.Count, WorkspaceBlock.MaxShortcuts); s++)
                    {
                        var item = b.Items[s];
                        var label = (item.Label ?? string.Empty).Trim();
                        var target = (item.Target ?? string.Empty).Trim();
                        if (label.Length is 0 or > 60 || label.Any(char.IsControl))
                            Add($"items[{s}].label", "Give the shortcut a label of up to 60 characters.");
                        var ok = item.Kind switch
                        {
                            WorkspaceBlock.DocTypeKind => registry.TryGet(target, out var m) && !m.IsChildTable,
                            WorkspaceBlock.DashboardKind => await repository.ExistsAsync(registry.Get(DashboardService.DocTypeName), target, ct),
                            WorkspaceBlock.PageKind => Pages.ContainsKey(target),
                            _ => false,
                        };
                        if (!ok)
                            Add($"items[{s}].target", item.Kind is WorkspaceBlock.PageKind ? "Choose one of the Desk's pages." : "Choose something that exists.");
                        items.Add(new WorkspaceShortcut(label, item.Kind ?? string.Empty, target));
                    }

                    cleaned.Add(new WorkspaceBlock { Type = b.Type, Items = items });
                    break;
                }
            }
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
        return (title, roles, cleaned);
    }

    public async Task<WorkspaceDetail> CreateAsync(WorkspaceRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var (title, roles, blocks) = await ValidateAsync(request, ct);
        if (await repository.CountAsync(Meta, new ListQuery(), ct) >= MaxWorkspaces)
            throw Invalid("title", $"At most {MaxWorkspaces} workspaces.");

        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["title"] = title;
        row["sequence"] = (long)request.Sequence;
        row["roles"] = string.Join('\n', roles);
        row["blocks"] = JsonSerializer.Serialize(blocks, Json);
        await repository.InsertAsync(Meta, row, null, ct);
        return await GetAsync((string)row["name"]!, user, ct);
    }

    public async Task<WorkspaceDetail> UpdateAsync(string id, WorkspaceRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var existing = await repository.GetAsync(Meta, id, ct) ?? throw new NotFoundException("Workspace not found.");
        var (title, roles, blocks) = await ValidateAsync(request, ct);
        var stored = (DateTime)existing["modified"]!;
        var changes = new Dictionary<string, object?>
        {
            ["title"] = title, ["sequence"] = (long)request.Sequence, ["roles"] = string.Join('\n', roles), ["blocks"] = JsonSerializer.Serialize(blocks, Json),
            ["modified"] = history.Now() > stored ? history.Now() : stored.AddTicks(10), ["modified_by"] = user.User,
        };
        await repository.UpdateAsync(Meta, id, changes, null, null, ct);
        return await GetAsync(id, user, ct);
    }

    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        if (!await repository.DeleteAsync(Meta, id, null, ct))
            throw new NotFoundException("Workspace not found.");
    }
}
