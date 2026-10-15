using System.Globalization;
using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>One entry of a user's notification list.</summary>
public sealed record NotificationItem(
    string Id, string Type, string DocType, string DocName, string From, string? FromName, string Subject, bool Read, DateTime At);

public sealed record FollowState(bool Following, long Followers);

/// <summary>One assignment of a document to a person (a ToDo row), with names resolved for display.</summary>
public sealed record AssignmentInfo(
    string Id, string User, string? UserName, string AssignedBy, string? AssignedByName, string Status, string Priority,
    string? Due, string? Description, DateTime At, string? DocType = null, string? DocName = null);

/// <summary>Finds @someone@example.com mentions in comment text.</summary>
public static partial class Mentions
{
    public const int Max = 10;

    [GeneratedRegex(@"(?<![\w.+\-@])@([A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,})")]
    private static partial Regex MentionRegex();

    public static IReadOnlyList<string> Find(string text) =>
        MentionRegex().Matches(text).Select(m => m.Groups[1].Value.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal).Take(Max).ToList();
}

/// <summary>
/// In-app notifications: a row per person per event. Before anything is written the recipient must exist, be enabled,
/// and be allowed to read the document, so a notification can never reveal a document (or a comment on it) to someone
/// who could not open it.
/// </summary>
public sealed class NotificationService(
    IMetaRegistry registry, IDocumentRepository repository, Lazy<DocumentService> documents, HistoryRecorder history)
{
    public const string DocTypeName = "Notification Log";
    public const int MaxPage = 50;
    private const int MaxSubject = 500;

    private DocTypeMeta? Meta => registry.TryGet(DocTypeName, out var meta) ? meta : null;

    /// <summary>The recipient as the document engine sees them: their roles, or null if they are not an enabled user.</summary>
    public async Task<UserContext?> ContextOfAsync(string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email) || !registry.TryGet("User", out var userMeta))
            return null;

        var user = await repository.GetAsync(userMeta, email, ct);
        if (user is null || (user.GetValueOrDefault("enabled") is { } enabled && !UserPermissionService.IsTrue(enabled)))
            return null;

        var roles = registry.TryGet("Has Role", out var hasRole)
            ? (await repository.ListAsync(hasRole, new ListQuery
            {
                Filters = [new QueryFilter("user", FilterOp.Eq, email)],
                Fields = ["role"],
                OrderBy = "name",
                Descending = false,
                Limit = 1000,
            }, ct)).Select(r => (string)r["role"]!).ToList()
            : [];
        return new UserContext(email, roles);
    }

    /// <summary>Whether this person may open the document, judged by exactly the rules that apply when they ask for it.</summary>
    public async Task<bool> CanReadAsync(IUserContext person, string doctype, string name, CancellationToken ct = default)
    {
        try
        {
            await documents.Value.GetAsync(doctype, name, person, ct);
            return true;
        }
        catch (Exception ex) when (ex is PermissionDeniedException or NotFoundException or UnauthenticatedException)
        {
            return false;
        }
    }

    public async Task<string?> NameOfAsync(string email, CancellationToken ct = default) =>
        registry.TryGet("User", out var userMeta) ? (await repository.GetAsync(userMeta, email, ct))?.GetValueOrDefault("full_name") as string : null;

    /// <summary>Tells one person something about a document. Silently does nothing if they should not hear it.</summary>
    /// <summary>
    /// Adds to a person's notifications, if they are an enabled user who may read the document and are not the one who caused it.
    /// A notification from a rule carries the rule and a key (the day): the same person is not told twice for the same key.
    /// </summary>
    /// <returns>True when a notification was written.</returns>
    public async Task<bool> NotifyAsync(
        string forUser, string type, string doctype, string name, string fromUser, string subject, CancellationToken ct = default,
        string? rule = null, string? ruleKey = null)
    {
        if (Meta is not { } meta || string.Equals(forUser, fromUser, StringComparison.Ordinal))
            return false;

        var person = await ContextOfAsync(forUser, ct);
        if (person is null || !await CanReadAsync(person, doctype, name, ct))
            return false;

        if (rule is not null && ruleKey is not null)
        {
            var already = await repository.ListAsync(meta, new ListQuery
            {
                Filters = [new("for_user", FilterOp.Eq, forUser), new("document_type", FilterOp.Eq, doctype), new("document_name", FilterOp.Eq, name),
                           new("rule", FilterOp.Eq, rule), new("rule_key", FilterOp.Eq, ruleKey)],
                Fields = ["name"], Limit = 1,
            }, ct);
            if (already.Count > 0)
                return false;
        }

        var row = HistoryRecorder.Standard(fromUser, history.Now());
        row["for_user"] = forUser;
        row["notification_type"] = type;
        row["document_type"] = doctype;
        row["document_name"] = name;
        row["from_user"] = fromUser;
        row["subject"] = subject.Length > MaxSubject ? subject[..MaxSubject] + "…" : subject;
        row["is_read"] = false;
        row["rule"] = rule;
        row["rule_key"] = ruleKey;
        await repository.InsertAsync(meta, row, null, ct);
        return true;
    }

    private IEnumerable<QueryFilter> Mine(IUserContext user, bool unreadOnly)
    {
        yield return new QueryFilter("for_user", FilterOp.Eq, user.User);
        if (unreadOnly)
            yield return new QueryFilter("is_read", FilterOp.Eq, false);
    }

    public async Task<IReadOnlyList<NotificationItem>> ListAsync(
        IUserContext user, bool unreadOnly, int limit, DateTime? before, CancellationToken ct = default)
    {
        RequireUser(user);
        if (Meta is not { } meta)
            return [];

        var filters = Mine(user, unreadOnly).ToList();
        if (before is not null)
            filters.Add(new QueryFilter("creation", FilterOp.Lt, before.Value));
        var rows = await repository.ListAsync(meta, new ListQuery
        {
            Filters = filters, OrderBy = "creation", Descending = true, Limit = Math.Clamp(limit, 1, MaxPage),
        }, ct);

        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var from in rows.Select(r => (string)r["from_user"]!).Distinct(StringComparer.Ordinal))
            names[from] = await NameOfAsync(from, ct);
        return rows.Select(r => new NotificationItem(
            (string)r["name"]!, (string)r["notification_type"]!, (string)r["document_type"]!, (string)r["document_name"]!,
            (string)r["from_user"]!, names.GetValueOrDefault((string)r["from_user"]!), (string)r["subject"]!,
            UserPermissionService.IsTrue(r["is_read"]), (DateTime)r["creation"]!)).ToList();
    }

    public async Task<long> UnreadCountAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireUser(user);
        return Meta is { } meta ? await repository.CountAsync(meta, new ListQuery { Filters = [.. Mine(user, unreadOnly: true)] }, ct) : 0;
    }

    /// <summary>Marks one of your own notifications as read. Someone else's looks like it does not exist.</summary>
    public async Task MarkReadAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireUser(user);
        var meta = Meta ?? throw new NotFoundException("Notification not found.");
        var row = await repository.GetAsync(meta, id, ct);
        if (row is null || !string.Equals(row["for_user"] as string, user.User, StringComparison.Ordinal))
            throw new NotFoundException("Notification not found.");

        await repository.UpdateAsync(meta, id, new Dictionary<string, object?> { ["is_read"] = true }, null, null, ct);
    }

    public async Task MarkAllReadAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireUser(user);
        if (Meta is not { } meta)
            return;

        foreach (var row in await repository.ListAsync(meta, new ListQuery
        {
            Filters = [.. Mine(user, unreadOnly: true)], Fields = ["name"], OrderBy = "name", Descending = false, Limit = 1000,
        }, ct))
        {
            await repository.UpdateAsync(meta, (string)row["name"]!, new Dictionary<string, object?> { ["is_read"] = true }, null, null, ct);
        }
    }

    private static void RequireUser(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
    }
}

/// <summary>Following a document: you hear about changes and comments. People follow what they create, comment on or are assigned.</summary>
public sealed class FollowService(
    IMetaRegistry registry, IDocumentRepository repository, Lazy<DocumentService> documents, HistoryRecorder history)
{
    public const string DocTypeName = "Document Follow";
    public const int MaxFollowers = 50;

    private DocTypeMeta? Meta => registry.TryGet(DocTypeName, out var meta) ? meta : null;

    private IEnumerable<QueryFilter> Of(string doctype, string name)
    {
        yield return new QueryFilter("ref_doctype", FilterOp.Eq, doctype);
        yield return new QueryFilter("ref_docname", FilterOp.Eq, name);
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(string doctype, string name, string? user, int limit, CancellationToken ct)
    {
        if (Meta is not { } meta)
            return [];
        var filters = Of(doctype, name).ToList();
        if (user is not null)
            filters.Add(new QueryFilter("user", FilterOp.Eq, user));
        return await repository.ListAsync(meta, new ListQuery { Filters = filters, OrderBy = "creation", Descending = false, Limit = limit }, ct);
    }

    /// <summary>Starts following without any check (the framework follows on someone's behalf after it checked their access).</summary>
    public async Task EnsureAsync(string doctype, string name, string user, CancellationToken ct = default)
    {
        if (Meta is not { } meta || (await RowsAsync(doctype, name, user, 1, ct)).Count > 0)
            return;

        var row = HistoryRecorder.Standard(user, history.Now());
        row["ref_doctype"] = doctype;
        row["ref_docname"] = name;
        row["user"] = user;
        await repository.InsertAsync(meta, row, null, ct);
    }

    public async Task<IReadOnlyList<string>> FollowersAsync(string doctype, string name, CancellationToken ct = default) =>
        (await RowsAsync(doctype, name, null, MaxFollowers, ct)).Select(r => (string)r["user"]!).ToList();

    public async Task FollowAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        await documents.Value.GetAsync(doctype, name, user, ct); // you must be able to read it
        await EnsureAsync(doctype, name, user.User, ct);
    }

    public async Task UnfollowAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        await documents.Value.GetAsync(doctype, name, user, ct);
        if (Meta is not { } meta)
            return;
        foreach (var row in await RowsAsync(doctype, name, user.User, 10, ct))
            await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
    }

    public async Task<FollowState> StateAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        await documents.Value.GetAsync(doctype, name, user, ct);
        if (Meta is not { } meta)
            return new FollowState(false, 0);
        var following = (await RowsAsync(doctype, name, user.User, 1, ct)).Count > 0;
        return new FollowState(following, await repository.CountAsync(meta, new ListQuery { Filters = [.. Of(doctype, name)] }, ct));
    }
}

/// <summary>What the document engine and comments tell the collaboration features: a document was saved, a comment was written.</summary>
public sealed class CollaborationHooks(
    FollowService follows, NotificationService notifications, IMetaRegistry registry, IDocumentRepository repository)
{
    // Where each feature keeps the DocType and name of the document it is about.
    private static readonly (string DocType, string TypeField, string NameField)[] Tables =
    [
        (AssignmentService.DocTypeName, "reference_doctype", "reference_name"),
        (FollowService.DocTypeName, "ref_doctype", "ref_docname"),
        (NotificationService.DocTypeName, "document_type", "document_name"),
        (AutoRepeatService.DocTypeName, "reference_doctype", "reference_name"),
        (FileService.DocTypeName, "reference_doctype", "reference_name"),
    ];

    /// <summary>References to a document's name in assignments, follows and notifications, for rename and merge.</summary>
    public IEnumerable<ReferenceUpdate> References(string doctype)
    {
        foreach (var (table, typeField, nameField) in Tables)
        {
            if (registry.TryGet(table, out var meta))
                yield return new ReferenceUpdate(meta, nameField, typeField, doctype);
        }
    }

    /// <summary>A deleted document takes its assignments, followers and notifications with it.</summary>
    public async Task DeleteForDocumentAsync(string doctype, string docName, CancellationToken ct = default)
    {
        foreach (var (table, typeField, nameField) in Tables)
        {
            if (!registry.TryGet(table, out var meta))
                continue;

            var rows = await repository.ListAsync(meta, new ListQuery
            {
                Filters = [new QueryFilter(typeField, FilterOp.Eq, doctype), new QueryFilter(nameField, FilterOp.Eq, docName)],
                Fields = ["name"],
                OrderBy = "name",
                Descending = false,
                Limit = 1000,
            }, ct);
            foreach (var row in rows)
                await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
        }
    }

    private static string Describe(string doctype, string name) => doctype == name ? doctype : $"{doctype} {name}";

    private static bool Applies(DocTypeMeta meta) => meta.TrackChanges && !meta.IsChildTable && meta.Module != "Core";

    public async Task AfterSaveAsync(DocTypeMeta meta, string docName, bool isNew, IUserContext actor, CancellationToken ct = default)
    {
        if (!Applies(meta) || !actor.IsAuthenticated)
            return;

        if (isNew)
        {
            await follows.EnsureAsync(meta.Name, docName, actor.User, ct);
            return;
        }

        var followers = (await follows.FollowersAsync(meta.Name, docName, ct)).Where(f => f != actor.User).ToList();
        if (followers.Count == 0)
            return;

        var who = await notifications.NameOfAsync(actor.User, ct) ?? actor.User;
        foreach (var follower in followers)
            await notifications.NotifyAsync(follower, "Update", meta.Name, docName, actor.User, $"{who} changed {Describe(meta.Name, docName)}", ct);
    }

    /// <summary>A comment was written: the author follows, people mentioned and followers are told (only if they can read the document).</summary>
    public async Task AfterCommentAsync(string doctype, string docName, string content, IUserContext author, CancellationToken ct = default)
    {
        await follows.EnsureAsync(doctype, docName, author.User, ct);
        var who = await notifications.NameOfAsync(author.User, ct) ?? author.User;
        var mentioned = Mentions.Find(content);
        var excerpt = content.Length > 100 ? content[..100] + "…" : content;

        foreach (var person in mentioned)
            await notifications.NotifyAsync(person, "Mention", doctype, docName, author.User, $"{who} mentioned you in {Describe(doctype, docName)}: {excerpt}", ct);
        foreach (var follower in (await follows.FollowersAsync(doctype, docName, ct)).Where(f => f != author.User && !mentioned.Contains(f)))
            await notifications.NotifyAsync(follower, "Comment", doctype, docName, author.User, $"{who} commented on {Describe(doctype, docName)}", ct);
    }
}

/// <summary>
/// Assigning a document to a person: a ToDo row they see in "My assignments" and get a notification for. Assigning needs the
/// same right as sharing, because the assignee is given read access if they lack it.
/// </summary>
public sealed class AssignmentService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    ShareManager shares,
    NotificationService notifications,
    FollowService follows,
    HistoryRecorder history)
{
    public const string DocTypeName = "ToDo";
    public const int MaxDescription = 500;
    public const int MaxMine = 100;
    private static readonly string[] Priorities = ["Low", "Medium", "High"];

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private async Task<AssignmentInfo> ToInfoAsync(Dictionary<string, object?> row, bool withDocument, CancellationToken ct)
    {
        var user = (string)row["allocated_to"]!;
        var by = (string)row["owner"]!;
        return new AssignmentInfo(
            (string)row["name"]!, user, await notifications.NameOfAsync(user, ct), by, await notifications.NameOfAsync(by, ct),
            row["status"] as string ?? "Open", row["priority"] as string ?? "Medium",
            row["date"] is DateOnly due ? due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            row["description"] as string, (DateTime)row["creation"]!,
            withDocument ? (string)row["reference_doctype"]! : null, withDocument ? (string)row["reference_name"]! : null);
    }

    private async Task NoteAsync(string doctype, string name, string text, string user, CancellationToken ct)
    {
        if (history.InfoNote(doctype, name, text, user, history.Now()) is { } note)
            await repository.InsertAsync(note.Meta, note.Inserts[0], null, ct);
    }

    public async Task<IReadOnlyList<AssignmentInfo>> ForDocumentAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        await documents.GetAsync(doctype, name, user, ct);
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name), new("status", FilterOp.Ne, "Cancelled")],
            OrderBy = "creation", Descending = false, Limit = 100,
        }, ct);
        var result = new List<AssignmentInfo>();
        foreach (var row in rows)
            result.Add(await ToInfoAsync(row, false, ct));
        return result;
    }

    /// <summary>Open (or closed) assignments of the signed-in person, with the document each refers to.</summary>
    public async Task<IReadOnlyList<AssignmentInfo>> MineAsync(IUserContext user, string status, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (status is not ("Open" or "Closed"))
            throw new DocumentValidationException([new FieldError("status", "Use Open or Closed.")]);

        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("allocated_to", FilterOp.Eq, user.User), new("status", FilterOp.Eq, status)],
            OrderBy = "creation", Descending = true, Limit = MaxMine,
        }, ct);
        var result = new List<AssignmentInfo>();
        foreach (var row in rows)
            result.Add(await ToInfoAsync(row, true, ct));
        return result;
    }

    public async Task<AssignmentInfo> AssignAsync(
        string doctype, string name, string? assignee, string? description, string? due, string? priority, IUserContext actor, CancellationToken ct = default)
    {
        await shares.EnsureCanShareAsync(doctype, name, actor, ct); // reads the document and checks the actor may hand it on

        var person = await notifications.ContextOfAsync((assignee ?? string.Empty).Trim(), ct)
            ?? throw new DocumentValidationException([new FieldError("allocated_to", "Choose an existing, enabled user.")]);
        description = (description ?? string.Empty).Trim();
        if (description.Length > MaxDescription || description.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new DocumentValidationException([new FieldError("description", $"Keep the description to {MaxDescription} characters of plain text.")]);
        priority = string.IsNullOrWhiteSpace(priority) ? "Medium" : priority;
        if (!Priorities.Contains(priority))
            throw new DocumentValidationException([new FieldError("priority", "Use Low, Medium or High.")]);
        DateOnly? dueDate = null;
        if (!string.IsNullOrWhiteSpace(due))
        {
            if (!DateOnly.TryParseExact(due.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                throw new DocumentValidationException([new FieldError("date", "Use a date like 2026-12-31.")]);
            dueDate = parsed;
        }

        return await CreateAsync(doctype, name, person, description, priority, dueDate, rule: null, actor, ct);
    }

    private async Task<AssignmentInfo> CreateAsync(
        string doctype, string name, UserContext person, string description, string priority, DateOnly? dueDate, string? rule, IUserContext actor, CancellationToken ct)
    {
        var existing = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name),
                       new("allocated_to", FilterOp.Eq, person.User), new("status", FilterOp.Eq, "Open")],
            Limit = 1,
        }, ct);
        if (existing.Count > 0 && rule is null)
            return await ToInfoAsync(existing[0], false, ct); // already assigned to them: nothing to add

        // The assignee must be able to open what they are asked to work on.
        if (!await notifications.CanReadAsync(person, doctype, name, ct))
        {
            if (rule is null)
                await shares.SetAsync(doctype, name, person.User, read: true, write: false, actor, ct);
            else
                await shares.GrantReadAsync(doctype, name, person.User, ct);
        }

        var row = HistoryRecorder.Standard(actor.User, history.Now());
        row["allocated_to"] = person.User;
        row["reference_doctype"] = doctype;
        row["reference_name"] = name;
        row["status"] = "Open";
        row["priority"] = priority;
        row["date"] = dueDate;
        row["description"] = description.Length == 0 ? null : description;
        row["assignment_rule"] = rule;
        await repository.InsertAsync(Meta, row, null, ct);

        var who = await notifications.NameOfAsync(actor.User, ct) ?? actor.User;
        var target = await notifications.NameOfAsync(person.User, ct) ?? person.User;
        await NoteAsync(doctype, name, rule is null ? $"Assigned to {target}" : $"Assigned to {target} by the rule \"{rule}\"", actor.User, ct);
        await follows.EnsureAsync(doctype, name, person.User, ct);
        var what = doctype == name ? doctype : $"{doctype} {name}";
        var detail = description.Length > 0 ? ": " + (description.Length > 100 ? description[..100] + "…" : description) : string.Empty;
        await notifications.NotifyAsync(person.User, "Assignment", doctype, name, actor.User,
            rule is null ? $"{who} assigned you {what}{detail}" : $"You were assigned {what} by the rule \"{rule}\"{detail}", ct);
        return await ToInfoAsync(row, false, ct);
    }

    /// <summary>The assignments assignment rules made for a document (any status), for the rule engine.</summary>
    internal async Task<IReadOnlyList<Dictionary<string, object?>>> RuleAssignmentsAsync(string doctype, string name, CancellationToken ct)
    {
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            OrderBy = "creation", Descending = false, Limit = 200,
        }, ct);
        return rows.Where(r => r["assignment_rule"] is string { Length: > 0 }).ToList();
    }

    /// <summary>A rule gives a document to someone. The person who saved the document is shown as the one who assigned it.</summary>
    internal Task<AssignmentInfo> AssignByRuleAsync(string doctype, string name, UserContext person, AssignmentRule rule, IUserContext actor, CancellationToken ct)
    {
        DateOnly? due = rule.DueInDays is { } days ? DateOnly.FromDateTime(history.Now()).AddDays(days) : null;
        return CreateAsync(doctype, name, person, rule.Description ?? string.Empty, rule.Priority, due, rule.Name, actor, ct);
    }

    /// <summary>A rule's close condition holds: the assignment is withdrawn.</summary>
    internal async Task WithdrawForRuleAsync(string id, string rule, IUserContext actor, CancellationToken ct)
    {
        var row = await repository.GetAsync(Meta, id, ct);
        if (row is null || row["status"] as string != "Open")
            return;

        await repository.UpdateAsync(Meta, id, new Dictionary<string, object?> { ["status"] = "Cancelled", ["modified"] = history.Now(), ["modified_by"] = actor.User }, null, null, ct);
        var assignee = (string)row["allocated_to"]!;
        await NoteAsync((string)row["reference_doctype"]!, (string)row["reference_name"]!,
            $"Assignment of {await notifications.NameOfAsync(assignee, ct) ?? assignee} withdrawn by the rule \"{rule}\"", actor.User, ct);
    }

    private async Task<Dictionary<string, object?>> RequireAssignmentAsync(string id, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();

        // Only the two people involved (or an Administrator) can see that it exists.
        var row = await repository.GetAsync(Meta, id, ct);
        var involved = row is not null
            && (string.Equals(row["allocated_to"] as string, user.User, StringComparison.Ordinal)
                || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal)
                || permissions.IsAdministrator(user));
        return involved ? row! : throw new NotFoundException("Assignment not found.");
    }

    private async Task<AssignmentInfo> SetStatusAsync(string id, string status, IUserContext user, CancellationToken ct)
    {
        var row = await RequireAssignmentAsync(id, user, ct);
        if (row["status"] as string != "Open")
            throw new DocumentValidationException("This assignment is no longer open.");

        var now = history.Now();
        await repository.UpdateAsync(Meta, id, new Dictionary<string, object?> { ["status"] = status, ["modified"] = now, ["modified_by"] = user.User }, null, null, ct);
        row["status"] = status;

        var doctype = (string)row["reference_doctype"]!;
        var name = (string)row["reference_name"]!;
        var assignee = (string)row["allocated_to"]!;
        var by = (string)row["owner"]!;
        var assigneeName = await notifications.NameOfAsync(assignee, ct) ?? assignee;
        await NoteAsync(doctype, name, status == "Closed" ? $"{assigneeName} completed the assignment" : $"Assignment of {assigneeName} was removed", user.User, ct);
        if (status == "Closed")
        {
            await notifications.NotifyAsync(by, "Assignment", doctype, name, user.User,
                $"{assigneeName} completed the assignment on {(doctype == name ? doctype : $"{doctype} {name}")}", ct);
        }

        return await ToInfoAsync(row, true, ct);
    }

    /// <summary>The assignee (or the person who assigned, or an Administrator) marks it done.</summary>
    public Task<AssignmentInfo> CompleteAsync(string id, IUserContext user, CancellationToken ct = default) => SetStatusAsync(id, "Closed", user, ct);

    /// <summary>Takes the assignment back.</summary>
    public Task<AssignmentInfo> RemoveAsync(string id, IUserContext user, CancellationToken ct = default) => SetStatusAsync(id, "Cancelled", user, ct);
}
