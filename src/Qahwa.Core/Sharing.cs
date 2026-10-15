using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>What one document has been shared with one user. Write implies read.</summary>
public readonly record struct ShareGrant(bool Read, bool Write)
{
    public static ShareGrant None => default;

    public bool Allows(PermAction action) => action switch
    {
        PermAction.Read => Read || Write,
        PermAction.Write => Write,
        _ => false,
    };

    public ShareGrant Or(ShareGrant other) => new(Read || other.Read, Write || other.Write);
}

/// <summary>A document shared with someone, as shown to the person managing the shares.</summary>
public sealed record ShareInfo(string User, bool Read, bool Write, string? FullName = null);

/// <summary>Reads the DocShare rows the engine needs to decide access. Managing them is <see cref="ShareManager"/>'s job.</summary>
public sealed class ShareStore(IMetaRegistry registry, IDocumentRepository repository)
{
    public const string DocTypeName = "DocShare";
    private const int MaxRows = 1000;

    private DocTypeMeta? Meta => registry.TryGet(DocTypeName, out var meta) ? meta : null;

    internal static ShareGrant GrantOf(IReadOnlyDictionary<string, object?> row) =>
        new(UserPermissionService.IsTrue(row.GetValueOrDefault("can_read")), UserPermissionService.IsTrue(row.GetValueOrDefault("can_write")));

    private async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(IEnumerable<QueryFilter> filters, CancellationToken ct) =>
        Meta is { } meta
            ? await repository.ListAsync(meta, new ListQuery { Filters = [.. filters], OrderBy = "creation", Descending = false, Limit = MaxRows }, ct)
            : [];

    public async Task<ShareGrant> ForDocumentAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            return ShareGrant.None;

        var rows = await RowsAsync(
            [new("user", FilterOp.Eq, user.User), new("share_doctype", FilterOp.Eq, doctype), new("share_name", FilterOp.Eq, name)], ct);
        return rows.Select(GrantOf).Aggregate(ShareGrant.None, (a, b) => a.Or(b));
    }

    /// <summary>Every document of a DocType shared with the user, by name.</summary>
    public async Task<IReadOnlyDictionary<string, ShareGrant>> ForUserAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        var result = new Dictionary<string, ShareGrant>(StringComparer.Ordinal);
        if (!user.IsAuthenticated)
            return result;

        foreach (var row in await RowsAsync([new("user", FilterOp.Eq, user.User), new("share_doctype", FilterOp.Eq, doctype)], ct))
        {
            if (row.GetValueOrDefault("share_name") is not string name)
                continue;
            var grant = GrantOf(row);
            result[name] = result.TryGetValue(name, out var old) ? old.Or(grant) : grant;
        }

        return result;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> RowsForDocumentAsync(string doctype, string name, CancellationToken ct = default) =>
        await RowsAsync([new("share_doctype", FilterOp.Eq, doctype), new("share_name", FilterOp.Eq, name)], ct);

    /// <summary>Removes every share of a document, for when the document itself is deleted.</summary>
    public async Task DeleteForDocumentAsync(string doctype, string name, CancellationToken ct = default)
    {
        if (Meta is not { } meta)
            return;
        foreach (var row in await RowsForDocumentAsync(doctype, name, ct))
            await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
    }
}

/// <summary>Share a document with someone, change what they may do, or stop sharing.</summary>
public sealed class ShareManager(
    IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, PermissionService permissions, ShareStore store)
{
    private static readonly UserContext System = new(UserContext.AdministratorRole, [UserContext.AdministratorRole]);

    private async Task RequireSharerAsync(string doctype, string name, IUserContext actor, CancellationToken ct)
    {
        var meta = registry.Get(doctype);
        var doc = await documents.GetAsync(doctype, name, actor, ct); // also proves it exists and may be read
        if (permissions.IsAdministrator(actor))
            return;

        // Sharing is for people who could edit the document through their roles; a share alone does not let you pass it on.
        var level = permissions.Evaluate(meta, actor, PermAction.Write);
        var owns = string.Equals(doc.GetValueOrDefault("owner") as string, actor.User, StringComparison.Ordinal);
        if (level == AccessLevel.None || (level == AccessLevel.Owner && !owns))
            throw new PermissionDeniedException($"You may not share this {meta.Name}.");
    }

    public async Task<IReadOnlyList<ShareInfo>> ListAsync(string doctype, string name, IUserContext actor, CancellationToken ct = default)
    {
        await RequireSharerAsync(doctype, name, actor, ct);
        var result = new List<ShareInfo>();
        foreach (var row in await store.RowsForDocumentAsync(doctype, name, ct))
        {
            var user = (string)row["user"]!;
            var full = registry.TryGet("User", out var userMeta) ? (await repository.GetAsync(userMeta, user, ct))?.GetValueOrDefault("full_name") as string : null;
            var grant = ShareStore.GrantOf(row);
            result.Add(new ShareInfo(user, grant.Read || grant.Write, grant.Write, full));
        }

        return result;
    }

    /// <summary>
    /// Users the document could be shared with, matching what was typed (by name or email), for the Share panel's picker.
    /// Only people who may share this document can ask, and only enabled users other than themselves are offered.
    /// </summary>
    public async Task<IReadOnlyList<LinkOption>> SearchUsersAsync(
        string doctype, string name, string? search, IUserContext actor, CancellationToken ct = default)
    {
        await RequireSharerAsync(doctype, name, actor, ct);
        if (!registry.TryGet("User", out var userMeta))
            return [];

        search = (search ?? string.Empty).Trim();
        if (search.Length > 100)
            search = search[..100];

        async Task<IReadOnlyList<Dictionary<string, object?>>> Find(string field) =>
            await repository.ListAsync(userMeta, new ListQuery
            {
                Filters = [new QueryFilter("enabled", FilterOp.Eq, true), .. (search.Length == 0 ? [] : new[] { new QueryFilter(field, FilterOp.Like, "%" + search + "%") })],
                Fields = ["name", "email", "full_name"],
                OrderBy = field == "email" ? "email" : "full_name",
                Descending = false,
                Limit = 20,
            }, ct);

        var rows = new List<Dictionary<string, object?>>(await Find("full_name"));
        if (search.Length > 0)
        {
            foreach (var row in await Find("email"))
            {
                if (rows.All(r => !Equals(r["name"], row["name"])))
                    rows.Add(row);
            }
        }

        return rows
            .Where(r => !string.Equals(r["name"] as string, actor.User, StringComparison.Ordinal))
            .Take(20)
            .Select(r => new LinkOption((string)r["name"]!, r.GetValueOrDefault("full_name") as string is { Length: > 0 } full ? full : (string)r["name"]!, (string)r["name"]!))
            .ToList();
    }

    /// <summary>Shares the document with a user, or changes what they may do. With neither read nor write, the share is removed.</summary>
    public async Task SetAsync(string doctype, string name, string? user, bool read, bool write, IUserContext actor, CancellationToken ct = default)
    {
        await RequireSharerAsync(doctype, name, actor, ct);
        user = user?.Trim() ?? string.Empty;
        if (user.Length == 0 || !registry.TryGet("User", out var userMeta) || !await repository.ExistsAsync(userMeta, user, ct))
            throw new DocumentValidationException([new FieldError("user", "Choose an existing user.")]);

        var existing = (await store.RowsForDocumentAsync(doctype, name, ct))
            .Where(r => string.Equals(r["user"] as string, user, StringComparison.Ordinal)).ToList();
        read |= write;

        if (!read)
        {
            foreach (var row in existing)
                await documents.DeleteAsync(ShareStore.DocTypeName, (string)row["name"]!, System, ct);
            return;
        }

        var values = new Dictionary<string, object?> { ["can_read"] = true, ["can_write"] = write };
        if (existing.Count > 0)
        {
            await documents.UpdateAsync(ShareStore.DocTypeName, (string)existing[0]["name"]!, values, System, ct);
            foreach (var extra in existing.Skip(1))
                await documents.DeleteAsync(ShareStore.DocTypeName, (string)extra["name"]!, System, ct);
            return;
        }

        values["user"] = user;
        values["share_doctype"] = doctype;
        values["share_name"] = name;
        await documents.InsertAsync(ShareStore.DocTypeName, values, System, ct);
    }
}
