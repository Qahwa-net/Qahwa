using System.Globalization;
using System.Net.Mail;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>One email, ready to hand to a mail server: plain text only.</summary>
public sealed record EmailMessage(string To, string Subject, string Body);

/// <summary>Delivers an email. Throws when it could not be sent; the queue then tries again later.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <param name="To">Email addresses (at most 10).</param>
/// <param name="Template">Name of an Email Template to fill from the document; its subject and message are used when the request has none.</param>
public sealed record EmailRequest(IReadOnlyList<string>? To = null, string? Subject = null, string? Message = null, string? Template = null);

public sealed record EmailPreview(string Subject, string Message);

public sealed record EmailTemplateInfo(string Name, string? DocType);

public sealed record EmailRun(int Sent, int Failed);

/// <summary>
/// Email: a queue of plain-text messages, filled from templates, sent by the scheduler through an <see cref="IEmailSender"/>.
/// Sending from a document needs write access to it; every person is limited per day, so the server cannot be used to spam.
/// </summary>
public sealed class EmailService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    HistoryRecorder history,
    IEmailSender sender)
{
    public const string QueueName = "Email Queue";
    public const string TemplateName = "Email Template";
    public const int MaxRecipients = 10;
    public const int MaxSubject = 200;
    public const int MaxBody = 20000;
    public const int MaxAttempts = 3;
    public const int DailyLimit = 50;
    private const int BatchSize = 50;
    private static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);

    private DocTypeMeta Queue => registry.Get(QueueName);

    /// <summary>A plain address (<c>name@host</c>): no display name, no line breaks, nothing a mail server could read as a header.</summary>
    public static bool ValidAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Length > 254 || address.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '<' or '>' or ',' or ';' or '"'))
            return false;
        return MailAddress.TryCreate(address, out var parsed) && parsed.Address == address && parsed.Host.Contains('.');
    }

    private static string OneLine(string? text) => new string((text ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    /// <summary>Puts emails in the queue, one per address. Returns how many were queued.</summary>
    public async Task<int> EnqueueAsync(
        IEnumerable<string> to, string subject, string body, string? doctype, string? name, string owner, CancellationToken ct = default, int limit = MaxRecipients)
    {
        subject = OneLine(subject);
        if (subject.Length == 0 || subject.Length > MaxSubject)
            throw Invalid("subject", $"Write a subject of up to {MaxSubject} characters.");
        body = (body ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (body.Length == 0 || body.Length > MaxBody)
            throw Invalid("message", $"Write a message of up to {MaxBody} characters.");

        var addresses = to.Select(a => a?.Trim() ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (addresses.Count == 0 || addresses.Count > limit)
            throw Invalid("to", $"Give between 1 and {limit} email addresses.");
        if (addresses.FirstOrDefault(a => !ValidAddress(a)) is { } bad)
            throw Invalid("to", $"'{OneLine(bad)[..Math.Min(bad.Length, 60)]}' is not an email address.");

        var now = history.Now();
        foreach (var address in addresses)
        {
            var row = HistoryRecorder.Standard(owner, now);
            row["recipient"] = address;
            row["subject"] = subject;
            row["body"] = body;
            row["status"] = "Pending";
            row["attempts"] = 0L;
            row["reference_doctype"] = doctype;
            row["reference_name"] = name;
            await repository.InsertAsync(Queue, row, null, ct);
        }

        return addresses.Count;
    }

    /// <summary>Templates anyone signed in can pick from for a DocType (those made for it, and those for any).</summary>
    public async Task<IReadOnlyList<EmailTemplateInfo>> TemplatesAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(registry.Get(TemplateName), new ListQuery { OrderBy = "name", Descending = false, Limit = 200 }, ct);
        return rows.Where(r => r["reference_doctype"] is not string { Length: > 0 } ref1 || string.Equals(ref1, doctype, StringComparison.OrdinalIgnoreCase))
            .Select(r => new EmailTemplateInfo((string)r["name"]!, r["reference_doctype"] as string)).ToList();
    }

    private async Task<(DocTypeMeta Meta, Dictionary<string, object?> Doc)> LoadAsync(string doctype, string name, IUserContext user, CancellationToken ct)
    {
        var doc = await documents.GetAsync(doctype, name, user, ct); // also proves the person may read it
        return (registry.Get(doctype), doc);
    }

    private async Task<EmailPreview> FillAsync(string template, DocTypeMeta meta, string name, IReadOnlyDictionary<string, object?> doc, string person, CancellationToken ct)
    {
        var row = await repository.GetAsync(registry.Get(TemplateName), template, ct) ?? throw new NotFoundException($"Email template '{template}' was not found.");
        if (row["reference_doctype"] is string { Length: > 0 } only && !string.Equals(only, meta.Name, StringComparison.OrdinalIgnoreCase))
            throw Invalid("template", $"This template is for {only} documents.");

        object? Value(string field) => field switch
        {
            "name" => name,
            "doctype" => meta.Name,
            "user" => person,
            _ => doc.GetValueOrDefault(field),
        };
        return new EmailPreview(MessageTemplate.Render((string)(row["subject"] ?? string.Empty), Value), MessageTemplate.Render((string)(row["body"] ?? string.Empty), Value));
    }

    /// <summary>The subject and message a template gives for one document, for the dialog to show before sending.</summary>
    public async Task<EmailPreview> PreviewAsync(string doctype, string name, string template, IUserContext user, CancellationToken ct = default)
    {
        var (meta, doc) = await LoadAsync(doctype, name, user, ct);
        return await FillAsync(template, meta, name, doc, user.User, ct);
    }

    /// <summary>Emails about a document, from the person using it. Needs write access; at most <see cref="DailyLimit"/> a day per person.</summary>
    public async Task<int> SendFromDocumentAsync(string doctype, string name, EmailRequest request, IUserContext user, CancellationToken ct = default)
    {
        var (meta, doc) = await LoadAsync(doctype, name, user, ct);
        if (permissions.Evaluate(meta, user, PermAction.Write) == AccessLevel.None)
            throw new PermissionDeniedException("You need permission to change this document to email about it.");

        var subject = request.Subject;
        var message = request.Message;
        if (!string.IsNullOrWhiteSpace(request.Template))
        {
            var filled = await FillAsync(request.Template, meta, name, doc, user.User, ct);
            subject = string.IsNullOrWhiteSpace(subject) ? filled.Subject : subject;
            message = string.IsNullOrWhiteSpace(message) ? filled.Message : message;
        }

        var recipients = request.To ?? [];
        var startOfDay = history.Now().Date;
        var today = await repository.CountAsync(Queue, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("creation", FilterOp.Ge, startOfDay)],
        }, ct);
        if (today + Math.Max(recipients.Count, 1) > DailyLimit)
            throw Invalid(string.Empty, $"You can send at most {DailyLimit} emails a day.");

        var count = await EnqueueAsync(recipients, subject ?? string.Empty, message ?? string.Empty, doctype, name, user.User, ct);
        if (history.InfoNote(doctype, name, $"Emailed {string.Join(", ", recipients.Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))}", user.User, history.Now()) is { } note)
            await repository.InsertAsync(note.Meta, note.Inserts[0], null, ct);
        return count;
    }

    /// <summary>Sends what is waiting. Each message is claimed first, so several servers never send it twice; a failure is retried up to three times.</summary>
    public async Task<EmailRun> SendPendingAsync(CancellationToken ct = default)
    {
        var meta = Queue;
        var stuck = history.Now() - StuckAfter;
        var due = await repository.ListAsync(meta, new ListQuery
        {
            Filters = [new("status", FilterOp.In, new List<object?> { "Pending", "Sending" }), new("attempts", FilterOp.Lt, (long)MaxAttempts)],
            OrderBy = "creation", Descending = false, Limit = BatchSize,
        }, ct);

        int sent = 0, failed = 0;
        foreach (var row in due)
        {
            // A message "being sent" for a long time belongs to a server that died: it is ours to take.
            if (row["status"] as string == "Sending" && (DateTime)row["modified"]! > stuck)
                continue;

            var attempts = Convert.ToInt64(row["attempts"] ?? 0L, CultureInfo.InvariantCulture) + 1;
            // The claim must change `modified` (it is the version the next server compares with), even on a clock that has not moved.
            var modified = (DateTime)row["modified"]!;
            var claim = new Dictionary<string, object?> { ["status"] = "Sending", ["attempts"] = attempts, ["modified"] = history.Now() > modified ? history.Now() : modified.AddTicks(10) };
            if (!await repository.UpdateAsync(meta, (string)row["name"]!, claim, (DateTime)row["modified"]!, null, ct))
                continue;

            var outcome = new Dictionary<string, object?> { ["modified"] = history.Now() };
            try
            {
                await sender.SendAsync(new EmailMessage((string)row["recipient"]!, (string)row["subject"]!, row["body"] as string ?? string.Empty), ct);
                outcome["status"] = "Sent";
                outcome["sent_at"] = history.Now();
                outcome["last_error"] = null;
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var reason = OneLine(ex.Message);
                outcome["last_error"] = reason.Length > 500 ? reason[..500] : reason;
                outcome["status"] = attempts >= MaxAttempts ? "Failed" : "Pending";
                failed++;
            }

            await repository.UpdateAsync(meta, (string)row["name"]!, outcome, null, null, ct);
        }

        return new EmailRun(sent, failed);
    }
}
