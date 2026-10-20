using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record ExportResult(IReadOnlyList<string> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated);

/// <summary>One choice in a Link field: the stored value, what to show for it, and a smaller hint (usually the ID).</summary>
public sealed record LinkOption(string Value, string Label, string? Description);

/// <summary>How many documents have one value of a field (null is "empty").</summary>
public sealed record GroupCountItem(string? Value, long Count);

public sealed record ReportRow(string? Group, decimal Value);

public sealed record ReportResult(IReadOnlyList<ReportRow> Rows, bool Truncated);

/// <summary>Single and tree DocTypes, export and grouped reports, rename and merge, module blocking.</summary>
public sealed partial class DocumentService
{
    /// <summary>The most rows an export or a report reads.</summary>
    public const int ExportLimit = 10_000;

    private static readonly string[] Aggregates = ["count", "sum", "avg", "min", "max"];

    private static bool IsTrue(object? value) => UserPermissionService.IsTrue(value);

    /// <summary>
    /// A Role Profile hands out roles, so choosing one on a User needs the same right as adding a Has Role document:
    /// otherwise anyone allowed to edit users could give themselves or others any role through a profile.
    /// </summary>
    private void CheckProfileAssignment(DocTypeMeta meta, IReadOnlyDictionary<string, object?> changes, IUserContext user, List<FieldError> errors)
    {
        // Editing a profile changes the roles of everyone who has it, so that needs the right as well.
        var editsProfile = meta.Name == "Role Profile";
        var choosesProfile = meta.Name == "User" && changes.TryGetValue("role_profile", out var profile) && profile is string { Length: > 0 };
        if (!editsProfile && !choosesProfile)
            return;
        if (registry.TryGet("Has Role", out var hasRole) && permissions.Evaluate(hasRole, user, PermAction.Create) == AccessLevel.None)
            errors.Add(new FieldError(editsProfile ? string.Empty : "role_profile", "You may not assign roles, so you cannot change or choose role profiles."));
    }

    // ---- Modules -----------------------------------------------------------------------------------------------

    /// <summary>A Module Profile can take a whole module away from a user.</summary>
    private async Task EnsureModuleAsync(DocTypeMeta meta, IUserContext user, CancellationToken ct)
    {
        if (profiles is null || meta.Module is null)
            return;
        if ((await profiles.BlockedModulesAsync(user, ct)).Contains(meta.Module))
            throw new PermissionDeniedException($"You do not have access to the {meta.Module} module.");
    }

    /// <summary>Like <see cref="ListMetas"/>, without the DocTypes of the modules blocked for this user.</summary>
    public async Task<IReadOnlyList<DocTypeMeta>> ListMetasAsync(IUserContext user, CancellationToken ct = default)
    {
        var metas = ListMetas(user);
        if (profiles is null)
            return metas;

        var blocked = await profiles.BlockedModulesAsync(user, ct);
        return blocked.Count == 0 ? metas : metas.Where(m => m.Module is null || !blocked.Contains(m.Module)).ToList();
    }

    // ---- Single DocTypes -----------------------------------------------------------------------------------------

    /// <summary>What a single DocType shows before anyone has saved it: its defaults, with the DocType's name as the document's name.</summary>
    private Dictionary<string, object?> SingleSkeleton(DocTypeMeta meta)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        ApplyDefaults(meta, values, []);
        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && !values.ContainsKey(f.Name)))
            values[field.Name] = null;
        foreach (var field in TableFields(meta))
            values[field.Name] = new List<Dictionary<string, object?>>();
        values["name"] = meta.Name;
        values["docstatus"] = 0;
        values["owner"] = null;
        return values;
    }

    // ---- Tree DocTypes -------------------------------------------------------------------------------------------

    /// <summary>A node needs a parent that is a group and must not become its own ancestor; a group with children stays a group.</summary>
    private async Task ValidateTreeAsync(
        DocTypeMeta meta,
        string? name,
        IReadOnlyDictionary<string, object?> merged,
        IReadOnlyDictionary<string, object?>? existing,
        List<FieldError> errors,
        CancellationToken ct)
    {
        if (!meta.IsTree)
            return;

        const string Parent = DocTypeMetaExtensions.TreeParentField;
        const string Group = DocTypeMetaExtensions.TreeGroupField;

        if (merged.GetValueOrDefault(Parent) is string { Length: > 0 } parentName)
        {
            if (name is not null && string.Equals(parentName, name, StringComparison.Ordinal))
            {
                errors.Add(new FieldError(Parent, "A node cannot be its own parent."));
            }
            else if (await repository.GetAsync(meta, parentName, ct) is { } parent)
            {
                if (!IsTrue(parent.GetValueOrDefault(Group)))
                    errors.Add(new FieldError(Parent, $"'{parentName}' is not a group, so it cannot have children."));
                else if (name is not null && await IsDescendantAsync(meta, parentName, name, ct))
                    errors.Add(new FieldError(Parent, $"'{parentName}' is below '{name}', so it cannot be its parent."));
            }
        }

        if (name is not null && existing is not null && IsTrue(existing.GetValueOrDefault(Group)) && !IsTrue(merged.GetValueOrDefault(Group))
            && await HasChildrenAsync(meta, name, ct))
        {
            errors.Add(new FieldError(Group, "This node has children, so it must stay a group."));
        }
    }

    private async Task<bool> HasChildrenAsync(DocTypeMeta meta, string name, CancellationToken ct) =>
        await repository.CountAsync(meta, new ListQuery { Filters = [new QueryFilter(DocTypeMetaExtensions.TreeParentField, FilterOp.Eq, name)] }, ct) > 0;

    /// <summary>True when <paramref name="node"/> is <paramref name="ancestor"/> or lies below it. The walk is bounded so bad data cannot loop it.</summary>
    private async Task<bool> IsDescendantAsync(DocTypeMeta meta, string node, string ancestor, CancellationToken ct)
    {
        var current = node;
        for (var depth = 0; depth < 1000 && !string.IsNullOrEmpty(current); depth++)
        {
            if (string.Equals(current, ancestor, StringComparison.Ordinal))
                return true;
            current = (await repository.GetAsync(meta, current, ct))?.GetValueOrDefault(DocTypeMetaExtensions.TreeParentField) as string;
        }

        return false;
    }

    /// <summary>The children of one node (or the roots when <paramref name="parent"/> is empty), as the tree view shows them.</summary>
    public async Task<IReadOnlyList<Dictionary<string, object?>>> TreeChildrenAsync(
        string doctype, string? parent, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        if (!meta.IsTree)
            throw General($"{meta.Name} is not a tree.");

        var fields = new List<string> { "name", DocTypeMetaExtensions.TreeGroupField };
        if (meta.TitleField is not null && meta.TitleField != "name")
            fields.Add(meta.TitleField);
        var filter = string.IsNullOrEmpty(parent)
            ? new RawFilter(DocTypeMetaExtensions.TreeParentField, "is", "not set")
            : new RawFilter(DocTypeMetaExtensions.TreeParentField, "=", parent);

        var result = await ListAsync(doctype, new ListRequest
        {
            Filters = [filter],
            Fields = fields,
            OrderBy = "name",
            Descending = false,
            Limit = MaxLimit,
        }, user, ct);
        return result.Data.Select(r => new Dictionary<string, object?>(r)).ToList();
    }

    // ---- Link fields: search and friendly names ------------------------------------------------------------------

    /// <summary>The virtual link target that lets a field hold "the name of a DocType". Choices are the DocTypes the person may read.</summary>
    public const string DocTypeTarget = "DocType";

    public const int MaxLinkResults = 20;
    public const int MaxLinkTitles = 50;
    private const int MaxSearchLength = 100;

    /// <summary>The field that gives a document its human name: the title field, if the DocType has one.</summary>
    private static string? LabelField(DocTypeMeta meta) =>
        meta.TitleField is { } title && title != "name" && meta.Fields.Any(f => f.Name == title && FieldTypes.HasColumn(f.Type)) ? title : null;

    private static LinkOption ToOption(Dictionary<string, object?> row, string? labelField)
    {
        var name = (string)row["name"]!;
        var label = labelField is not null && row.GetValueOrDefault(labelField) is string { Length: > 0 } title ? title : name;
        return new LinkOption(name, label, label == name ? null : name);
    }

    /// <summary>
    /// Documents of the linked DocType matching what the user typed, by name or by title. Goes through the normal list
    /// rules, so the user only sees documents they may read; an empty search gives the most recent ones.
    /// </summary>
    public async Task<IReadOnlyList<LinkOption>> SearchLinkAsync(
        string doctype, string? search, int limit, IUserContext user, CancellationToken ct = default)
    {
        search = (search ?? string.Empty).Trim();
        if (search.Length > MaxSearchLength)
            search = search[..MaxSearchLength];
        limit = Math.Clamp(limit, 1, MaxLinkResults);
        if (doctype == DocTypeTarget)
            return (await ListMetasAsync(user, ct))
                .Where(m => search.Length == 0 || m.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || (m.Module?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Take(limit)
                .Select(m => new LinkOption(m.Name, m.Name, m.Module)).ToList();

        var meta = RequireTopLevel(registry.Get(doctype));

        var labelField = LabelField(meta);
        var fields = labelField is null ? new List<string> { "name" } : ["name", labelField];
        async Task<IReadOnlyList<Dictionary<string, object?>>> Find(string field) =>
            (await ListAsync(doctype, new ListRequest
            {
                Filters = search.Length == 0 ? [] : [new RawFilter(field, "like", "%" + search + "%")],
                Fields = fields,
                OrderBy = search.Length == 0 ? "modified" : "name",
                Descending = search.Length == 0,
                Limit = limit,
            }, user, ct)).Data;

        var rows = new List<Dictionary<string, object?>>(await Find("name"));
        if (search.Length > 0 && labelField is not null)
        {
            foreach (var row in await Find(labelField))
            {
                if (rows.All(r => !Equals(r["name"], row["name"])))
                    rows.Add(row);
            }
        }

        if (search.Length > 0)
        {
            // The DocType's own search fields too, but only those this person may read (a hidden field must not be searchable).
            var access = permissions.FieldAccessFor(meta, user, user.User, creating: false);
            foreach (var name in meta.SearchFields.Where(n => n != labelField && meta.Fields.FirstOrDefault(f => f.Name == n) is { } f && access.CanRead(f)))
            {
                foreach (var row in await Find(name))
                {
                    if (rows.All(r => !Equals(r["name"], row["name"])))
                        rows.Add(row);
                }
            }
        }

        return rows.Take(limit).Select(r => ToOption(r, labelField)).ToList();
    }

    /// <summary>The friendly names of documents already chosen (for showing "Acme Ltd" instead of "CUST-00001"). Unreadable ones are left out.</summary>
    public async Task<IReadOnlyDictionary<string, LinkOption>> LinkTitlesAsync(
        string doctype, IReadOnlyCollection<string> names, IUserContext user, CancellationToken ct = default)
    {
        if (doctype == DocTypeTarget)
        {
            var readable = (await ListMetasAsync(user, ct)).ToDictionary(m => m.Name, StringComparer.Ordinal);
            return names.Where(readable.ContainsKey).Distinct(StringComparer.Ordinal).Take(MaxLinkTitles)
                .ToDictionary(n => n, n => new LinkOption(n, n, readable[n].Module), StringComparer.Ordinal);
        }

        var meta = RequireTopLevel(registry.Get(doctype));
        var wanted = names.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).Take(MaxLinkTitles).ToList();
        if (wanted.Count == 0)
            return new Dictionary<string, LinkOption>();

        var labelField = LabelField(meta);
        var rows = (await ListAsync(doctype, new ListRequest
        {
            Filters = [new RawFilter("name", "in", wanted)],
            Fields = labelField is null ? ["name"] : ["name", labelField],
            OrderBy = "name",
            Descending = false,
            Limit = MaxLinkTitles,
        }, user, ct)).Data;
        return rows.Select(r => ToOption(r, labelField)).ToDictionary(o => o.Value, o => o, StringComparer.Ordinal);
    }

    // ---- Group-by counts (the list sidebar) -------------------------------------------------------------------

    public const int MaxGroups = 50;

    private static readonly HashSet<FieldType> Groupable = [FieldType.Select, FieldType.Link, FieldType.Check, FieldType.Data, FieldType.Email, FieldType.Phone, FieldType.Int, FieldType.Rating];
    private static readonly string[] GroupableStandard = ["owner", "modified_by", "docstatus"];

    private static string? Plain(object? value) => value switch
    {
        null => null,
        bool b => b ? "1" : "0",
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    /// <summary>
    /// How many documents have each value of a field, among the documents this person could list with these filters: the same
    /// permission layers as the list, so counts never include a document the person could not see. Only fields they may read.
    /// </summary>
    public async Task<IReadOnlyList<GroupCountItem>> GroupByAsync(
        string doctype, string field, ListRequest request, int limit, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var query = await PrepareListQueryAsync(meta, request with { Fields = null, OrderBy = null, Offset = 0 }, user, ct);

        var access = permissions.FieldAccessFor(meta, user, documentOwner: null, creating: false);
        var allowed = meta.Fields.Where(f => Groupable.Contains(f.Type) && FieldTypes.HasColumn(f.Type) && access.CanRead(f)).Select(f => f.Name)
            .Concat(GroupableStandard).ToHashSet(StringComparer.Ordinal);
        if (!allowed.Contains(field ?? string.Empty))
            throw new DocumentValidationException([new FieldError("group_by", "You cannot group by this field.")]);

        var counts = await repository.GroupCountAsync(meta, field!, query.Filters, Math.Clamp(limit, 1, MaxGroups), ct);
        return counts.Select(c => new GroupCountItem(Plain(c.Value), c.Count)).ToList();
    }

    // ---- Export and reports ---------------------------------------------------------------------------------------

    private static bool IsExportable(DocFieldMeta field) => FieldTypes.HasColumn(field.Type) && field.Type != FieldType.Password;

    private static readonly HashSet<FieldType> Numeric = [FieldType.Int, FieldType.Float, FieldType.Decimal, FieldType.Currency, FieldType.Percent, FieldType.Rating, FieldType.Duration];

    /// <summary>Reads up to <see cref="ExportLimit"/> rows through the normal list rules, so every permission still applies.</summary>
    private async Task<(List<Dictionary<string, object?>> Rows, bool Truncated)> ReadAllAsync(
        string doctype, ListRequest request, IUserContext user, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        var truncated = false;
        for (var offset = 0; ; offset += MaxLimit)
        {
            var page = await ListAsync(doctype, request with { Limit = MaxLimit, Offset = offset, OrderBy = request.OrderBy ?? "name", Descending = request.OrderBy is null ? false : request.Descending }, user, ct);
            rows.AddRange(page.Data);
            if (rows.Count >= ExportLimit)
            {
                truncated = page.Total > ExportLimit;
                rows = rows.Take(ExportLimit).ToList();
                break;
            }

            if (page.Data.Count < MaxLimit)
                break;
        }

        return (rows, truncated);
    }

    /// <summary>Documents for download. Needs the export right as well as read; password fields are never included.</summary>
    public async Task<ExportResult> ExportAsync(string doctype, ListRequest request, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Read);
        permissions.Require(meta, user, PermAction.Export);
        await EnsureModuleAsync(meta, user, ct);

        var access = permissions.FieldAccessFor(meta, user, documentOwner: null, creating: false);
        var columns = request.Fields is { Count: > 0 }
            ? request.Fields.Distinct(StringComparer.Ordinal).ToList()
            : new[] { "name" }.Concat(meta.Fields.Where(f => IsExportable(f) && access.CanRead(f)).Select(f => f.Name)).ToList();
        var passwords = meta.Fields.Where(f => f.Type == FieldType.Password).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (columns.Any(passwords.Contains))
            throw new DocumentValidationException([new FieldError("fields", "Password fields cannot be exported.")]);

        var (rows, truncated) = await ReadAllAsync(doctype, request with { Fields = columns }, user, ct);
        if (accessLog is not null)
            await accessLog.RecordAsync("Export", meta.Name, null, rows.Count, user, ct);
        return new ExportResult(columns, rows, truncated);
    }

    /// <summary>
    /// A grouped summary: count, or sum/avg/min/max of a numeric field, per value of another field. Needs the report right.
    /// Computed from the rows the user may read, so it cannot reveal more than the list does.
    /// </summary>
    public async Task<ReportResult> ReportAsync(
        string doctype, string groupBy, string function, string? valueField, ListRequest request, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Read);
        permissions.Require(meta, user, PermAction.Report);
        await EnsureModuleAsync(meta, user, ct);

        function = (function ?? string.Empty).Trim().ToLowerInvariant();
        if (!Aggregates.Contains(function))
            throw new DocumentValidationException([new FieldError("aggregate", $"Use one of: {string.Join(", ", Aggregates)}.")]);

        var columns = meta.ColumnTypes();
        if (!columns.TryGetValue(groupBy ?? string.Empty, out var groupType) || groupType == FieldType.Password)
            throw new DocumentValidationException([new FieldError("group_by", "Group by a field of this DocType.")]);

        var fields = new List<string> { groupBy! };
        if (function != "count")
        {
            if (valueField is null || !columns.TryGetValue(valueField, out var valueType) || !Numeric.Contains(valueType))
                throw new DocumentValidationException([new FieldError("field", "Choose a numeric field to summarise.")]);
            fields.Add(valueField);
        }

        var (rows, truncated) = await ReadAllAsync(doctype, request with { Fields = fields }, user, ct);
        var groups = rows.GroupBy(r => r.GetValueOrDefault(groupBy!) switch
        {
            null => null,
            bool b => b ? "1" : "0",
            var g => Convert.ToString(g, System.Globalization.CultureInfo.InvariantCulture),
        });
        var result = groups.Select(g =>
        {
            var numbers = function == "count" ? [] : g.Select(r => r.GetValueOrDefault(valueField!)).Where(v => v is not null)
                .Select(v => Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            var value = function switch
            {
                "count" => g.Count(),
                "sum" => numbers.Sum(),
                "avg" => numbers.Count == 0 ? 0 : numbers.Average(),
                "min" => numbers.Count == 0 ? 0 : numbers.Min(),
                _ => numbers.Count == 0 ? 0 : numbers.Max(),
            };
            return new ReportRow(g.Key, value);
        }).OrderBy(r => r.Group, StringComparer.Ordinal).ToList();
        return new ReportResult(result, truncated);
    }

    // ---- Rename and merge -----------------------------------------------------------------------------------------

    /// <summary>
    /// Gives a document a new name, or merges it into an existing one. Every link to it, its child rows, its shares and
    /// the User Permissions naming it follow. Needs write access through roles (a share is not enough), and delete
    /// access as well for a merge, which removes the old document.
    /// </summary>
    public async Task<string> RenameAsync(
        string doctype, string name, string newName, bool merge, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        if (!meta.AllowRename || meta.IsSingle)
            throw General($"{meta.Name} documents cannot be renamed.");
        await EnsureModuleAsync(meta, user, ct);

        var (level, share) = await PreAuthorizeAsync(meta, user, PermAction.Write, name, ct);
        var existing = await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        if (Settle(level, share, PermAction.Write, existing, user))
            throw new PermissionDeniedException($"Renaming a {meta.Name} needs write access through your roles.");
        RequireRestrictions(await RestrictionsAsync(meta, user, ct), existing, meta);
        await RequireAppPermissionAsync(meta, PermAction.Write, existing, user, ct);
        if (meta.IsSubmittable && Convert.ToInt32(existing.GetValueOrDefault("docstatus") ?? 0) == 1)
            throw General("A submitted document cannot be renamed. Cancel it first.");

        newName = (newName ?? string.Empty).Trim();
        if (newName.Length == 0 || newName.Length > FieldTypes.ShortTextLength || newName.Any(char.IsControl))
            throw new DocumentValidationException([new FieldError("new_name", $"Give a name of 1 to {FieldTypes.ShortTextLength} characters.")]);
        if (string.Equals(newName, name, StringComparison.Ordinal))
            return name;

        var target = await repository.GetAsync(meta, newName, ct);
        if (target is null && merge)
            throw General($"'{newName}' does not exist, so there is nothing to merge into.");
        if (target is not null)
        {
            if (!merge)
                throw new ConflictException($"A {meta.Name} named '{newName}' already exists. Choose merge to combine them.");

            // Merging removes the old document and changes the target, so both rights are needed on both documents.
            var deleteLevel = permissions.Require(meta, user, PermAction.Delete);
            permissions.RequireOwnership(deleteLevel, existing, user);
            var (targetLevel, targetShare) = await PreAuthorizeAsync(meta, user, PermAction.Write, newName, ct);
            if (Settle(targetLevel, targetShare, PermAction.Write, target, user))
                throw new PermissionDeniedException($"Merging into '{newName}' needs write access to it through your roles.");
            RequireRestrictions(await RestrictionsAsync(meta, user, ct), target, meta);
            if (meta.IsTree && await IsDescendantAsync(meta, newName, name, ct))
                throw General($"'{newName}' is below '{name}', so they cannot be merged.");
        }

        var references = new List<ReferenceUpdate>();
        foreach (var other in registry.All)
        {
            foreach (var field in other.Fields.Where(f => f.Type == FieldType.Link && string.Equals(f.Options, meta.Name, StringComparison.OrdinalIgnoreCase)))
                references.Add(new ReferenceUpdate(other, field.Name));
        }

        if (registry.TryGet(ShareStore.DocTypeName, out var shareMeta))
            references.Add(new ReferenceUpdate(shareMeta, "share_name", "share_doctype", meta.Name));
        if (registry.TryGet(UserPermissionService.DocTypeName, out var permissionMeta))
            references.Add(new ReferenceUpdate(permissionMeta, "for_value", "allow", meta.Name));

        if (history is not null)
            references.AddRange(history.References(meta.Name));
        if (collaboration is not null)
            references.AddRange(collaboration.References(meta.Name));

        var childTables = TableFields(meta).Select(f => registry.Get(f.Options!)).DistinctBy(c => c.Name).ToList();
        var log = history?.Version(meta, newName, merge ? "Merge" : "Rename", new Dictionary<string, object?> { ["from"] = name, ["to"] = newName }, user.User, history.Now());
        await repository.RenameAsync(meta, name, newName, merge, childTables, references, ct, log is null ? null : [log]);
        return newName;
    }
}
