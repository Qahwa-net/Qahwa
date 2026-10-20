using System.Net.Mail;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

/// <summary>How to read the mailbox: the server (always over TLS), the account and the folder.</summary>
public sealed record ImapSettings(string Host, int Port, bool StartTls, string User, string Password, string Folder);

/// <summary>One message as the system keeps it: addresses, subject, the date and the text (never HTML, never attachments).</summary>
public sealed record ReceivedMail(long Uid, string? MessageId, string? InReplyTo, string From, string? FromName, string Subject, DateTime Date, string Text);

/// <summary>What reading new mail found: the messages kept (oldest first), the folder's version, and the highest message number seen, including any skipped for being too large (but not those left for the next poll).</summary>
public sealed record MailboxBatch(IReadOnlyList<ReceivedMail> Messages, long UidValidity, long NewestUid);

/// <summary>Reads a mailbox. The real one speaks IMAP (MailKit); tests use a stand-in.</summary>
public interface IMailboxClient
{
    /// <summary>The messages numbered above <paramref name="afterUid"/> (at most <paramref name="max"/>, oldest first); messages over <paramref name="maxBytes"/> are skipped. With <paramref name="max"/> 0 nothing is downloaded and only the folder's version and highest number are reported.</summary>
    Task<MailboxBatch> FetchAsync(ImapSettings settings, long afterUid, int max, int maxBytes, CancellationToken ct = default);

    /// <summary>Connects and signs in, and says how many messages the folder holds. Nothing is read or changed.</summary>
    Task<int> TestAsync(ImapSettings settings, CancellationToken ct = default);
}

/// <summary>
/// Incoming mail (ADR-095): the site's mailbox is read now and then and each new message becomes a <c>Communication</c> document
/// (sender, subject, date and the plain text). Mail is read-only to the system: nothing is deleted, moved or marked as read. It never
/// follows links, runs or keeps attachments, or keeps HTML (only its text), and everything taken from a message is cut to a safe size.
/// </summary>
public sealed class IncomingMailService(
    IMetaRegistry registry, IDocumentRepository repository, EmailAccountService accounts, IMailboxClient mailbox, HistoryRecorder history)
{
    public const string DocTypeName = "Communication";
    public const int MaxPerPoll = 50;
    public const int MaxMessageBytes = 512 * 1024;
    public const int MaxText = 100_000;
    public const int MaxSubject = 300;

    private static string Clean(string? text, int max, bool keepLines)
    {
        var chars = (text ?? string.Empty).Where(c => !char.IsControl(c) || (keepLines && c is '\n' or '\t')).ToArray();
        var clean = new string(chars).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
        return clean.Length > max ? clean[..max] + "…" : clean;
    }

    private static string? Address(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Length <= 254 && MailAddress.TryCreate(text.Trim(), out var parsed) && parsed.Address == text.Trim() ? text.Trim().ToLowerInvariant() : null;

    /// <summary>Reads the mailbox once. Returns how many new messages were kept (0 when incoming mail is off).</summary>
    public async Task<int> PollAsync(CancellationToken ct = default)
    {
        var settings = await accounts.GetIncomingAsync(ct);
        if (settings is null)
            return 0;
        var meta = registry.Get(DocTypeName);
        var (last, validity) = await accounts.GetCursorAsync(ct);
        // The first time, only note where the folder ends: mail that was already there is not imported, only what arrives from now on.
        if (validity is null)
        {
            var start = await mailbox.FetchAsync(settings, 0, 0, MaxMessageBytes, ct);
            await accounts.SetCursorAsync(start.NewestUid, start.UidValidity, ct);
            return 0;
        }

        var batch = await mailbox.FetchAsync(settings, last, MaxPerPoll, MaxMessageBytes, ct);
        // A different UIDVALIDITY means the server renumbered the folder: the old numbers mean nothing, so note where it ends now and carry on from there.
        if (validity != batch.UidValidity)
        {
            await accounts.SetCursorAsync(batch.NewestUid, batch.UidValidity, ct);
            return 0;
        }

        var kept = 0;
        var newest = Math.Max(last, batch.NewestUid);
        foreach (var mail in batch.Messages.OrderBy(m => m.Uid).Take(MaxPerPoll))
        {
            var from = Address(mail.From);
            if (from is null)
                continue; // a message with no usable sender address is not kept
            var messageId = string.IsNullOrWhiteSpace(mail.MessageId) ? null : Clean(mail.MessageId, 250, keepLines: false);
            if (messageId is not null && (await repository.ListAsync(meta, new ListQuery { Filters = [new("message_id", FilterOp.Eq, messageId)], Fields = ["name"], Limit = 1 }, ct)).Count > 0)
                continue; // already kept (a second server, or the same message seen twice)

            var row = HistoryRecorder.Standard("Administrator", history.Now());
            row["communication_type"] = "Received";
            row["sender"] = from;
            row["sender_name"] = Clean(mail.FromName, 140, keepLines: false);
            row["subject"] = Clean(mail.Subject, MaxSubject, keepLines: false);
            row["content"] = Clean(mail.Text, MaxText, keepLines: true);
            row["message_id"] = messageId;
            row["in_reply_to"] = Clean(mail.InReplyTo, 250, keepLines: false) is { Length: > 0 } reply ? reply : null;
            row["received_at"] = mail.Date.Kind == DateTimeKind.Utc ? mail.Date : mail.Date.ToUniversalTime();
            await repository.InsertAsync(meta, row, null, ct);
            kept++;
        }

        if (newest != last)
            await accounts.SetCursorAsync(newest, batch.UidValidity, ct);
        return kept;
    }

    /// <summary>Tries the saved incoming settings (System Managers): signs in and says how many messages the folder has.</summary>
    public async Task<int> TestAsync(CancellationToken ct = default)
    {
        var settings = await accounts.GetIncomingAsync(ct) ?? throw new DocumentValidationException([new FieldError("imap_enabled", "Save and enable incoming mail in Email Account first.")]);
        return await mailbox.TestAsync(settings, ct);
    }
}
