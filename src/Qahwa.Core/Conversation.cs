using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>One message of a document's conversation: sent from Qahwa, or received by the site's mailbox and matched to the document.</summary>
/// <param name="Kind">"sent" or "received".</param>
/// <param name="Who">For a sent message the addresses it went to; for a received one the sender.</param>
public sealed record ConversationItem(string Kind, string Who, string? WhoName, string Subject, string Text, DateTime At, string? Status);

/// <summary>
/// Email threads on documents (ADR-153). An email sent from a document carries a short reference in its subject (<c>[QW-0a1b2c3d4e]</c>);
/// a reply that keeps it is matched to the document when the mailbox is read. The conversation is the emails sent about the document
/// and the replies matched to it, oldest first, shown to anyone who may read the document: the mail is part of its record.
/// </summary>
public sealed partial class ConversationService(IMetaRegistry registry, IDocumentRepository repository, NotificationService notifications)
{
    public const int MaxItems = 200;

    [GeneratedRegex(@"\bQW-([0-9a-f]{10})\b")]
    private static partial Regex ReferenceRule();

    private static readonly Regex Token = new(@"\s*\[QW-[0-9a-f]{10}\]", RegexOptions.Compiled);

    /// <summary>A new, unguessable reference for one outgoing message about a document.</summary>
    public static string NewReference() => "QW-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(5)).ToLowerInvariant();

    /// <summary>The document a subject refers to (by the reference in it), or null.</summary>
    public static async Task<(string DocType, string Name)?> FindReferenceAsync(IMetaRegistry registry, IDocumentRepository repository, string? subject, CancellationToken ct)
    {
        if (subject is null || ReferenceRule().Match(subject) is not { Success: true } match)
            return null;
        var rows = await repository.ListAsync(registry.Get("Email Queue"), new ListQuery
        {
            Filters = [new("thread_ref", FilterOp.Eq, "QW-" + match.Groups[1].Value)], Fields = ["reference_doctype", "reference_name"], Limit = 1,
        }, ct);
        return rows.Count == 1 && rows[0]["reference_doctype"] is string type && rows[0]["reference_name"] is string name ? (type, name) : null;
    }

    private static string Plain(string? subject) => Token.Replace(subject ?? string.Empty, string.Empty);

    /// <summary>The conversation about a document the person can read (anyone else gets "not found").</summary>
    public async Task<IReadOnlyList<ConversationItem>> ForDocumentAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!await notifications.CanReadAsync(user, doctype, name, ct))
            throw new NotFoundException($"{doctype} '{name}' was not found.");

        var items = new List<ConversationItem>();
        var sent = await repository.ListAsync(registry.Get("Email Queue"), new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name), new("thread_ref", FilterOp.IsNotNull)],
            OrderBy = "creation", Descending = false, Limit = 1000,
        }, ct);
        // One send to several people is one message: the rows share the reference.
        foreach (var message in sent.GroupBy(r => (string)r["thread_ref"]!))
        {
            var first = message.First();
            items.Add(new ConversationItem("sent", string.Join(", ", message.Select(r => (string)r["recipient"]!)), null, Plain(first["subject"] as string),
                first["body"] as string ?? string.Empty, first["sent_at"] as DateTime? ?? (DateTime)first["creation"]!,
                message.All(r => r["status"] as string == "Sent") ? "Sent" : message.Any(r => r["status"] as string == "Failed") ? "Failed" : "Waiting"));
        }

        var received = await repository.ListAsync(registry.Get("Communication"), new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            OrderBy = "received_at", Descending = false, Limit = 1000,
        }, ct);
        items.AddRange(received.Select(r => new ConversationItem("received", r["sender"] as string ?? string.Empty, r["sender_name"] as string, Plain(r["subject"] as string),
            r["content"] as string ?? string.Empty, r["received_at"] as DateTime? ?? (DateTime)r["creation"]!, null)));
        return items.OrderBy(i => i.At).TakeLast(MaxItems).ToList();
    }
}
