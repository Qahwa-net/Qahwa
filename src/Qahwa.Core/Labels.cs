using System.Globalization;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

public sealed record LikeState(bool Liked, long Count);

public sealed record TagCount(string Tag, long Count);

/// <summary>
/// Tags (short labels anyone who may change a document can add, such as "urgent") and likes (one per person per document) on the
/// documents people work with. Both are kept in their own tables, are removed with the document, follow it when it is renamed, and can be
/// used in a list filter as <c>_tag</c> and <c>_liked_by</c>.
/// </summary>
public sealed class LabelService(IMetaRegistry registry, IDocumentRepository repository, PermissionService permissions, Lazy<DocumentService> documents, HistoryRecorder history)
{
    public const string TagDocType = "Document Tag";
    public const string LikeDocType = "Document Like";
    public const int MaxTagsPerDocument = 20;
    public const int MaxTagLength = 40;

    /// <summary>The pseudo-fields a list filter may use.</summary>
    public const string TagFilter = "_tag";
    public const string LikedByFilter = "_liked_by";

    private DocTypeMeta Tags => registry.Get(TagDocType);

    private DocTypeMeta Likes => registry.Get(LikeDocType);

    /// <summary>A tag as it is stored: trimmed, in lower case, without control characters or commas.</summary>
    /// <exception cref="DocumentValidationException">The tag is empty or too long, or contains something not allowed.</exception>
    public static string NormalizeTag(string? tag)
    {
        var text = (tag ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
            throw new DocumentValidationException([new FieldError("tag", "A tag cannot be empty.")]);
        if (text.Length > MaxTagLength)
            throw new DocumentValidationException([new FieldError("tag", $"A tag can be at most {MaxTagLength} characters.")]);
        if (text.Any(c => char.IsControl(c) || c == ','))
            throw new DocumentValidationException([new FieldError("tag", "A tag cannot contain commas or control characters.")]);
        return text;
    }

    private DocTypeMeta Taggable(string doctype)
    {
        if (!registry.TryGet(doctype, out var meta) || !AssignmentRuleManager.Applies(meta))
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        return meta;
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(DocTypeMeta table, string doctype, string name, string? field, string? value, int limit, CancellationToken ct)
    {
        List<QueryFilter> filters = [new("ref_doctype", FilterOp.Eq, doctype), new("ref_docname", FilterOp.Eq, name)];
        if (field is not null)
            filters.Add(new QueryFilter(field, FilterOp.Eq, value));
        return await repository.ListAsync(table, new ListQuery { Filters = filters, OrderBy = "creation", Descending = false, Limit = limit }, ct);
    }

    // ---- tags -------------------------------------------------------------------------------------------------

    public async Task<IReadOnlyList<string>> TagsAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        Taggable(doctype);
        await documents.Value.GetAsync(doctype, name, user, ct); // you must be able to read it
        return (await RowsAsync(Tags, doctype, name, null, null, MaxTagsPerDocument, ct)).Select(r => (string)r["tag"]!).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Adds a tag (nothing happens when it is there already). Needs the right to change the document.</summary>
    public async Task<IReadOnlyList<string>> AddTagAsync(string doctype, string name, string? tag, IUserContext user, CancellationToken ct = default)
    {
        var meta = Taggable(doctype);
        var text = NormalizeTag(tag);
        await documents.Value.GetAsync(doctype, name, user, ct);
        permissions.Require(meta, user, PermAction.Write);

        var current = await RowsAsync(Tags, doctype, name, null, null, MaxTagsPerDocument + 1, ct);
        if (current.All(r => (string?)r["tag"] != text))
        {
            if (current.Count >= MaxTagsPerDocument)
                throw new DocumentValidationException([new FieldError("tag", $"A document can have at most {MaxTagsPerDocument} tags.")]);
            var row = HistoryRecorder.Standard(user.User, history.Now());
            row["ref_doctype"] = doctype;
            row["ref_docname"] = name;
            row["tag"] = text;
            await repository.InsertAsync(Tags, row, null, ct);
        }

        return await TagsAsync(doctype, name, user, ct);
    }

    public async Task<IReadOnlyList<string>> RemoveTagAsync(string doctype, string name, string? tag, IUserContext user, CancellationToken ct = default)
    {
        var meta = Taggable(doctype);
        var text = NormalizeTag(tag);
        await documents.Value.GetAsync(doctype, name, user, ct);
        permissions.Require(meta, user, PermAction.Write);
        foreach (var row in await RowsAsync(Tags, doctype, name, "tag", text, 10, ct))
            await repository.DeleteAsync(Tags, (string)row["name"]!, null, ct);
        return await TagsAsync(doctype, name, user, ct);
    }

    /// <summary>
    /// The tags in use on the documents this person can see (among the 1,000 most recently changed), with how many documents carry each,
    /// most used first. Only documents the person may read are counted, so a tag on someone else's private document is not revealed.
    /// </summary>
    public async Task<IReadOnlyList<TagCount>> PopularTagsAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        Taggable(doctype);
        var names = (await documents.Value.ListAsync(doctype, new ListRequest { Fields = ["name"], OrderBy = "modified", Limit = 1000 }, user, ct))
            .Data.Select(r => (object?)r["name"]).ToList();
        if (names.Count == 0)
            return [];

        var rows = await repository.ListAsync(Tags, new ListQuery
        {
            Filters = [new("ref_doctype", FilterOp.Eq, doctype), new("ref_docname", FilterOp.In, names)],
            Fields = ["tag"], Limit = 20_000,
        }, ct);
        return rows.GroupBy(r => (string)r["tag"]!, StringComparer.Ordinal)
            .Select(g => new TagCount(g.Key, g.Count())).OrderByDescending(t => t.Count).ThenBy(t => t.Tag, StringComparer.Ordinal).Take(100).ToList();
    }

    // ---- likes ------------------------------------------------------------------------------------------------

    public async Task<LikeState> LikeStateAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        Taggable(doctype);
        await documents.Value.GetAsync(doctype, name, user, ct);
        var count = await repository.CountAsync(Likes, new ListQuery { Filters = [new("ref_doctype", FilterOp.Eq, doctype), new("ref_docname", FilterOp.Eq, name)] }, ct);
        var mine = (await RowsAsync(Likes, doctype, name, "user", user.User, 1, ct)).Count > 0;
        return new LikeState(mine, count);
    }

    /// <summary>Likes (or takes the like back from) a document. Anyone who can read it may like it.</summary>
    public async Task<LikeState> SetLikeAsync(string doctype, string name, bool liked, IUserContext user, CancellationToken ct = default)
    {
        Taggable(doctype);
        await documents.Value.GetAsync(doctype, name, user, ct);
        var mine = await RowsAsync(Likes, doctype, name, "user", user.User, 10, ct);
        if (liked && mine.Count == 0)
        {
            var row = HistoryRecorder.Standard(user.User, history.Now());
            row["ref_doctype"] = doctype;
            row["ref_docname"] = name;
            row["user"] = user.User;
            await repository.InsertAsync(Likes, row, null, ct);
        }
        else if (!liked)
        {
            foreach (var row in mine)
                await repository.DeleteAsync(Likes, (string)row["name"]!, null, ct);
        }

        return await LikeStateAsync(doctype, name, user, ct);
    }

    // ---- list filters -----------------------------------------------------------------------------------------

    public static bool IsLabelFilter(RawFilter filter) => filter.Field is TagFilter or LikedByFilter;

    /// <summary>
    /// The list filter for <c>["_tag", "=", "urgent"]</c> or <c>["_liked_by", "=", "__user"]</c>: documents that carry the tag, or that the
    /// person (or the signed-in user, for "__user") has liked.
    /// </summary>
    public static QueryFilter ToQueryFilter(IMetaRegistry registry, string doctype, RawFilter filter, IUserContext? user)
    {
        if (!string.Equals(filter.Op, "=", StringComparison.Ordinal))
            throw new DocumentValidationException([new FieldError(filter.Field, "Use \"=\" to filter by tag or by who liked a document.")]);
        var value = FieldValueConverter.Unwrap(filter.Value) as string;
        if (filter.Field == TagFilter)
            return new QueryFilter("name", FilterOp.Marked, new MarkedBy(registry.Get(TagDocType), doctype, "tag", NormalizeTag(value)));

        if (value == "__user")
            value = user?.User;
        if (string.IsNullOrWhiteSpace(value))
            throw new DocumentValidationException([new FieldError(filter.Field, "Say who liked it.")]);
        return new QueryFilter("name", FilterOp.Marked, new MarkedBy(registry.Get(LikeDocType), doctype, "user", value));
    }
}
