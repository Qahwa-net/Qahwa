using System.Text.Json;
using System.Text.Json.Serialization;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>The kinds of metadata a bundle carries. All of it is data made in the Desk (no code). Webhooks are left out on purpose: their address and secret belong to one site.</summary>
public enum BundleKind
{
    DocType,
    Customization,
    Workflow,
    AssignmentRules,
    NotificationRules,
}

public sealed record BundleItem(BundleKind Kind, string DocType, JsonElement Definition);

/// <summary>A file that moves what was built in one site to another: the same JSON the stores keep, plus a header.</summary>
public sealed record AppBundle
{
    public const string Format = "qahwa-bundle";
    public const int Version = 1;
    public const int MaxItems = 1000;

    public string Name { get; init; } = string.Empty;
    public DateTime ExportedAt { get; init; }
    public IReadOnlyList<BundleItem> Items { get; init; } = [];
}

/// <param name="Ok">False when this item was refused; <paramref name="Message"/> says why.</param>
/// <param name="Action">create, update (or, in a preview, what would be done).</param>
public sealed record BundleItemResult(BundleKind Kind, string DocType, string Action, bool Ok, string Message);

public sealed record BundleReport(bool DryRun, int Applied, int Failed, IReadOnlyList<BundleItemResult> Items);

/// <summary>
/// Exports and imports what the Desk built, as one file. Importing goes through the same managers that the Desk uses, so every
/// definition is validated exactly as if typed in by hand, permissions apply, nothing runs code, and a bad item fails on its own
/// without stopping the others. Webhooks are never exported (they hold secrets and site-specific addresses).
/// </summary>
public sealed class BundleService(
    IMutableMetaRegistry registry,
    DocTypeDesigner designer,
    CustomizationManager customizations,
    WorkflowManager workflows,
    AssignmentRuleManager assignmentRules,
    NotificationRuleManager notificationRules,
    PermissionService permissions,
    TimeProvider clock)
{
    private static readonly JsonSerializerOptions FileOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = true,
    };

    private bool CanManage(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!CanManage(user))
            throw new PermissionDeniedException("Only a System Manager can export or import bundles.");
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public async Task<AppBundle> ExportAsync(string name, IReadOnlyCollection<BundleKind>? kinds, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        name = (name ?? string.Empty).Trim();
        if (name.Length > 100)
            throw new DocumentValidationException("The bundle name is too long.");
        var wanted = kinds is { Count: > 0 } ? kinds.ToHashSet() : Enum.GetValues<BundleKind>().ToHashSet();
        var items = new List<BundleItem>();

        foreach (var meta in registry.All.OrderBy(m => !m.IsChildTable).ThenBy(m => m.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var source = registry.SourceOf(meta.Name);
            if (wanted.Contains(BundleKind.DocType) && source == DocTypeSource.Custom)
            {
                var detail = await designer.GetAsync(meta.Name, user, ct);
                items.Add(new BundleItem(BundleKind.DocType, meta.Name, Parse(DocTypeLoader.Serialize(detail.Definition))));
            }
        }

        foreach (var meta in registry.All.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (wanted.Contains(BundleKind.Customization))
            {
                var view = await TryAsync(() => customizations.GetAsync(meta.Name, user, ct));
                if (view is { Customized: true })
                    items.Add(new BundleItem(BundleKind.Customization, meta.Name, Parse(DocTypeLoader.SerializeCustomization(view.Customization))));
            }

            if (wanted.Contains(BundleKind.Workflow))
            {
                var view = await TryAsync(() => workflows.GetAsync(meta.Name, user, ct));
                if (view is { Exists: true })
                    items.Add(new BundleItem(BundleKind.Workflow, meta.Name, Parse(DocTypeLoader.SerializeWorkflow(view.Workflow))));
            }

            if (wanted.Contains(BundleKind.AssignmentRules))
            {
                var view = await TryAsync(() => assignmentRules.GetAsync(meta.Name, user, ct));
                if (view is { Exists: true })
                    items.Add(new BundleItem(BundleKind.AssignmentRules, meta.Name, Parse(DocTypeLoader.SerializeAssignmentRules(view.Set))));
            }

            if (wanted.Contains(BundleKind.NotificationRules))
            {
                var view = await TryAsync(() => notificationRules.GetAsync(meta.Name, user, ct));
                if (view is { Exists: true })
                    items.Add(new BundleItem(BundleKind.NotificationRules, meta.Name, Parse(DocTypeLoader.SerializeNotificationRules(view.Set))));
            }

        }

        return new AppBundle { Name = name, ExportedAt = clock.GetUtcNow().UtcDateTime, Items = items };
    }

    /// <summary>The bundle as the text of a file.</summary>
    public static string ToJson(AppBundle bundle) =>
        JsonSerializer.Serialize(new
        {
            format = AppBundle.Format,
            version = AppBundle.Version,
            name = bundle.Name,
            exportedAt = bundle.ExportedAt,
            items = bundle.Items.Select(i => new { kind = i.Kind, doctype = i.DocType, definition = i.Definition }),
        }, FileOptions);

    /// <summary>Reads the text of a bundle file. Throws a validation error that says what is wrong with it.</summary>
    public static AppBundle FromJson(string json)
    {
        JsonElement root;
        try
        {
            root = Parse(json);
        }
        catch (JsonException)
        {
            throw new DocumentValidationException("This is not a bundle file (it is not valid JSON).");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.String || format.GetString() != AppBundle.Format)
            throw new DocumentValidationException("This is not a Qahwa bundle file.");
        if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != AppBundle.Version)
            throw new DocumentValidationException($"This bundle has a version this server does not understand (expected {AppBundle.Version}).");
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            throw new DocumentValidationException("The bundle has no list of items.");
        if (items.GetArrayLength() > AppBundle.MaxItems)
            throw new DocumentValidationException($"A bundle may hold at most {AppBundle.MaxItems} items.");

        var list = new List<BundleItem>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
                || !Enum.TryParse<BundleKind>(kind.GetString(), ignoreCase: false, out var parsed) || !Enum.IsDefined(parsed)
                || !item.TryGetProperty("doctype", out var doctype) || doctype.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(doctype.GetString())
                || doctype.GetString()!.Length > 140
                || !item.TryGetProperty("definition", out var definition) || definition.ValueKind != JsonValueKind.Object)
                throw new DocumentValidationException($"Item {list.Count + 1} of the bundle is not valid (it needs a known kind, a doctype and a definition).");
            list.Add(new BundleItem(parsed, doctype.GetString()!, definition.Clone()));
        }

        var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
        return new AppBundle { Name = name.Length > 100 ? name[..100] : name, Items = list };
    }

    /// <summary>
    /// Applies a bundle, or with <paramref name="dryRun"/> only checks it (it reads, parses and looks up; saving does the full validation).
    /// Custom DocTypes go first (child tables before the DocTypes that use them), then the rest.
    /// </summary>
    public async Task<BundleReport> ImportAsync(AppBundle bundle, bool dryRun, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var results = new List<BundleItemResult>();
        var ordered = bundle.Items.Select((item, index) => (item, index)).OrderBy(x => x.item.Kind).ThenBy(x => x.index).Select(x => x.item).ToList();
        var incoming = new HashSet<string>(ordered.Where(i => i.Kind == BundleKind.DocType).Select(i => i.DocType), StringComparer.Ordinal);
        var children = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var item in ordered.Where(i => i.Kind == BundleKind.DocType))
            children[item.DocType] = item.Definition.TryGetProperty("isChildTable", out var c) && c.ValueKind == JsonValueKind.True;
        ordered = ordered.OrderBy(i => i.Kind).ThenBy(i => i.Kind == BundleKind.DocType && children.GetValueOrDefault(i.DocType) ? 0 : 1).ToList();

        foreach (var item in ordered)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ApplyAsync(item, dryRun, incoming, user, ct));
            }
            catch (DocumentValidationException e)
            {
                results.Add(new BundleItemResult(item.Kind, item.DocType, string.Empty, false, string.Join(" ", e.Errors.Select(x => string.IsNullOrEmpty(x.Field) ? x.Message : $"{x.Field}: {x.Message}"))));
            }
            catch (DocTypeValidationException e)
            {
                results.Add(new BundleItemResult(item.Kind, item.DocType, string.Empty, false, e.Message));
            }
            catch (QahwaException e)
            {
                results.Add(new BundleItemResult(item.Kind, item.DocType, string.Empty, false, e.Message));
            }
            catch (JsonException)
            {
                results.Add(new BundleItemResult(item.Kind, item.DocType, string.Empty, false, "The definition is not in the right shape."));
            }
        }

        return new BundleReport(dryRun, results.Count(r => r.Ok), results.Count(r => !r.Ok), results);
    }

    private async Task<BundleItemResult> ApplyAsync(BundleItem item, bool dryRun, HashSet<string> incoming, IUserContext user, CancellationToken ct)
    {
        var text = item.Definition.GetRawText();
        var doctype = item.DocType;

        if (item.Kind == BundleKind.DocType)
        {
            var definition = DocTypeLoader.Parse(text);
            if (!string.Equals(definition.Name, doctype, StringComparison.Ordinal))
                return Fail(item, "The DocType's name does not match the item.");
            var source = registry.SourceOf(doctype);
            if (source is not null && source != DocTypeSource.Custom)
                return Fail(item, "This name belongs to a built-in DocType; a bundle can only carry DocTypes made in the Desk.");
            if (dryRun)
                return new BundleItemResult(item.Kind, doctype, source is null ? "create" : "update", true, "Would be applied.");
            if (source is null)
            {
                await designer.CreateAsync(definition, user, ct);
                return new BundleItemResult(item.Kind, doctype, "create", true, "Created.");
            }

            var current = await designer.GetAsync(doctype, user, ct);
            await designer.UpdateAsync(doctype, definition, current.Modified, user, ct);
            return new BundleItemResult(item.Kind, doctype, "update", true, "Updated.");
        }

        if (registry.SourceOf(doctype) is null && !incoming.Contains(doctype))
            return Fail(item, $"'{doctype}' does not exist on this site and is not in the bundle.");

        switch (item.Kind)
        {
            case BundleKind.Customization:
            {
                var value = DocTypeLoader.ParseCustomization(text);
                if (dryRun)
                    return Preview(item, await ExistsAsync(() => customizations.GetAsync(doctype, user, ct), v => v.Customized));
                var current = await customizations.GetAsync(doctype, user, ct);
                await customizations.SaveAsync(doctype, value, current.Customized ? current.Modified : null, user, ct);
                return new BundleItemResult(item.Kind, doctype, current.Customized ? "update" : "create", true, "Applied.");
            }
            case BundleKind.Workflow:
            {
                var value = DocTypeLoader.ParseWorkflow(text);
                if (dryRun)
                    return Preview(item, await ExistsAsync(() => workflows.GetAsync(doctype, user, ct), v => v.Exists));
                var current = await workflows.GetAsync(doctype, user, ct);
                await workflows.SaveAsync(doctype, value, current.Exists ? current.Modified : null, user, ct);
                return new BundleItemResult(item.Kind, doctype, current.Exists ? "update" : "create", true, "Applied.");
            }
            case BundleKind.AssignmentRules:
            {
                var value = DocTypeLoader.ParseAssignmentRules(text);
                if (dryRun)
                    return Preview(item, await ExistsAsync(() => assignmentRules.GetAsync(doctype, user, ct), v => v.Exists));
                var current = await assignmentRules.GetAsync(doctype, user, ct);
                await assignmentRules.SaveAsync(doctype, value, current.Exists ? current.Modified : null, user, ct);
                return new BundleItemResult(item.Kind, doctype, current.Exists ? "update" : "create", true, "Applied.");
            }
            case BundleKind.NotificationRules:
            {
                var value = DocTypeLoader.ParseNotificationRules(text);
                if (dryRun)
                    return Preview(item, await ExistsAsync(() => notificationRules.GetAsync(doctype, user, ct), v => v.Exists));
                var current = await notificationRules.GetAsync(doctype, user, ct);
                await notificationRules.SaveAsync(doctype, value, current.Exists ? current.Modified : null, user, ct);
                return new BundleItemResult(item.Kind, doctype, current.Exists ? "update" : "create", true, "Applied.");
            }
            default:
                return Fail(item, "Unknown kind.");
        }
    }

    private static BundleItemResult Fail(BundleItem item, string message) => new(item.Kind, item.DocType, string.Empty, false, message);

    private static BundleItemResult Preview(BundleItem item, bool exists) =>
        new(item.Kind, item.DocType, exists ? "update" : "create", true, "Would be applied.");

    private static async Task<bool> ExistsAsync<T>(Func<Task<T>> get, Func<T, bool> exists)
    {
        try
        {
            return exists(await get());
        }
        catch (QahwaException)
        {
            return false;
        }
    }

    private static async Task<T?> TryAsync<T>(Func<Task<T>> get) where T : class
    {
        try
        {
            return await get();
        }
        catch (QahwaException)
        {
            return null; // not a DocType this kind applies to (child tables, singles, framework DocTypes for some kinds)
        }
    }
}
