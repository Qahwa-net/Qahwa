using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

/// <summary>What a link in an email would do, shown on the confirmation page.</summary>
public sealed record WorkflowLinkInfo(string DocType, string Name, string? Title, string State, string Action, string To);

/// <summary>
/// Approve or reject from an email. When a document enters a state with "email approvers" on, every person who can act on it
/// gets one link per action. A link is only a key to a confirmation page: opening it changes nothing (mail scanners open links),
/// the person must sign in as the one the email was written for and press the button, and then the ordinary workflow rules apply
/// to them. Links are random, stored only as a hash, work once, expire after seven days, and die when the document moves on.
/// </summary>
public sealed class WorkflowLinkService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    WorkflowCatalog catalog,
    NotificationService notifications,
    EmailService email,
    EmailAccountService accounts,
    IOptions<AuthOptions> options,
    HistoryRecorder history)
{
    public const string DocTypeName = "Workflow Link";
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private const int MaxPeople = 50;
    private const string SystemUser = "system@qahwa";

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Forgets the links of a document (it moved on, so they no longer apply).</summary>
    internal static async Task ClearAsync(IMetaRegistry registry, IDocumentRepository repository, string doctype, string name, CancellationToken ct)
    {
        if (!registry.TryGet(DocTypeName, out var meta))
            return;
        var rows = await repository.ListAsync(meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            Fields = ["name"], OrderBy = "creation", Descending = false, Limit = 1000,
        }, ct);
        foreach (var row in rows)
            await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
    }

    /// <summary>Emails the people who can act on a document that has just entered a state that asks for it. Returns how many emails were queued.</summary>
    public async Task<int> SendAsync(string doctype, string name, CancellationToken ct = default)
    {
        if (catalog.For(doctype) is not { } workflow)
            return 0;
        var baseUrl = options.Value.PublicUrl is { Length: > 0 } configured ? configured.TrimEnd('/') : await accounts.PublicUrlAsync(ct);
        if (baseUrl is null)
            return 0; // no address to put in a link: say nothing rather than send a broken one

        var system = new UserContext(SystemUser, [UserContext.AdministratorRole]);
        var doc = await documents.GetAsync(doctype, name, system, ct);
        var state = workflow.States.FirstOrDefault(s => doc.GetValueOrDefault(workflow.StateField) is string text && s.Name == text);
        if (state is not { EmailApprovers: true })
            return 0;

        var roles = workflow.Transitions.Where(t => t.From == state.Name).SelectMany(t => t.Roles).Distinct(StringComparer.Ordinal).ToList();
        if (roles.Count == 0 || !registry.TryGet("Has Role", out var hasRole))
            return 0;
        var people = (await repository.ListAsync(hasRole, new ListQuery
        {
            Filters = [new("role", FilterOp.In, roles.Cast<object?>().ToList())],
            Fields = ["user"], OrderBy = "name", Descending = false, Limit = MaxPeople,
        }, ct)).Select(r => (string)r["user"]!).Distinct(StringComparer.Ordinal);

        var meta = registry.Get(doctype);
        var title = meta.TitleField is { } field && field != "name" && doc.GetValueOrDefault(field) is string t ? $"{name} ({t})" : name;
        var now = history.Now();
        var queued = 0;
        foreach (var person in people)
        {
            if (!EmailService.ValidAddress(person) || await notifications.ContextOfAsync(person, ct) is not { } who)
                continue;
            var actions = (await documents.WorkflowStatusAsync(doctype, name, who, ct))?.Actions ?? [];
            if (actions.Count == 0)
                continue;

            var lines = new List<string>();
            foreach (var action in actions)
            {
                var token = NewToken();
                var row = HistoryRecorder.Standard(SystemUser, now);
                row["reference_doctype"] = doctype;
                row["reference_name"] = name;
                row["state"] = state.Name;
                row["action"] = action.Action;
                row["user"] = person;
                row["token_hash"] = Hash(token);
                row["expires"] = now.Add(Lifetime);
                await repository.InsertAsync(Meta, row, null, ct);
                lines.Add($"{action.Action}: {baseUrl}/workflow-action/{token}");
            }

            var body = $"{meta.Name} {title} is now \"{state.Name}\" and waiting for you.\n\n{string.Join("\n", lines)}\n\n"
                + "Each link opens a page where you sign in and confirm. Nothing happens until you do. The links work once and expire in 7 days.";
            queued += await email.EnqueueAsync([person], $"{meta.Name} {name}: action needed", body, doctype, name, SystemUser, ct);
        }

        return queued;
    }

    /// <summary>The row a token opens, only for the person it was written for and only while it still applies; otherwise "not found" (never why).</summary>
    private async Task<(Dictionary<string, object?> Row, WorkflowDefinition Workflow)> FindAsync(string? token, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var notFound = new NotFoundException("This link is not valid any more. Open the document to see what you can do.");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100)
            throw notFound;

        var rows = await repository.ListAsync(Meta, new ListQuery { Filters = [new("token_hash", FilterOp.Eq, Hash(token))], Limit = 1 }, ct);
        if (rows.Count == 0)
            throw notFound;
        var row = rows[0];
        if (!string.Equals(row["user"] as string, user.User, StringComparison.Ordinal) || (DateTime)row["expires"]! <= history.Now())
            throw notFound;

        var doctype = (string)row["reference_doctype"]!;
        var name = (string)row["reference_name"]!;
        var workflow = catalog.For(doctype);
        if (workflow is null)
            throw notFound;
        var doc = await documents.GetAsync(doctype, name, user, ct); // also proves they may still read it
        if (!string.Equals(doc.GetValueOrDefault(workflow.StateField) as string, row["state"] as string, StringComparison.Ordinal))
            throw notFound; // someone else already moved it
        return (row, workflow);
    }

    public async Task<WorkflowLinkInfo> InfoAsync(string? token, IUserContext user, CancellationToken ct = default)
    {
        var (row, workflow) = await FindAsync(token, user, ct);
        var doctype = (string)row["reference_doctype"]!;
        var name = (string)row["reference_name"]!;
        var action = (string)row["action"]!;
        var step = workflow.Transitions.FirstOrDefault(t => t.From == (string)row["state"]! && t.Action == action)
            ?? throw new NotFoundException("This link is not valid any more. Open the document to see what you can do.");
        var meta = registry.Get(doctype);
        var doc = await documents.GetAsync(doctype, name, user, ct);
        var title = meta.TitleField is { } field && field != "name" ? doc.GetValueOrDefault(field) as string : null;
        return new WorkflowLinkInfo(doctype, name, title, (string)row["state"]!, action, step.To);
    }

    /// <summary>Does the action the link stands for, as the signed-in person. A link works once.</summary>
    public async Task<WorkflowLinkInfo> ConfirmAsync(string? token, IUserContext user, CancellationToken ct = default)
    {
        var info = await InfoAsync(token, user, ct);
        var (row, _) = await FindAsync(token, user, ct);
        // Spend the link first: a second click (or a second server) finds nothing.
        if (!await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct))
            throw new NotFoundException("This link is not valid any more. Open the document to see what you can do.");
        await documents.ApplyWorkflowActionAsync(info.DocType, info.Name, info.Action, null, user, ct);
        return info;
    }
}
