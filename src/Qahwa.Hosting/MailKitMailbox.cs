using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;
using Qahwa.Core;

namespace Qahwa.Hosting;

/// <summary>
/// Reads the mailbox over IMAP with MailKit. The folder is opened <b>read-only</b> (so nothing is marked as read, moved or deleted), the
/// connection is always encrypted (TLS from the start, or STARTTLS), certificates are checked, a message larger than the limit is never
/// downloaded, and only the plain text of a message is taken: attachments and HTML are dropped.
/// </summary>
public class MailKitMailbox : IMailboxClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>A hook for tests that talk to a mail server with a certificate nobody trusts. The real mailbox never changes how certificates are checked.</summary>
    protected virtual void Configure(ImapClient client)
    {
    }

    private async Task<ImapClient> ConnectAsync(ImapSettings settings, CancellationToken ct)
    {
        var client = new ImapClient { Timeout = (int)Timeout.TotalMilliseconds, CheckCertificateRevocation = true };
        Configure(client);
        try
        {
            await client.ConnectAsync(settings.Host, settings.Port, settings.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(settings.User, settings.Password, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string name, CancellationToken ct)
    {
        var folder = string.Equals(name, "INBOX", StringComparison.OrdinalIgnoreCase) ? client.Inbox : await client.GetFolderAsync(name, ct);
        await folder.OpenAsync(FolderAccess.ReadOnly, ct);
        return folder;
    }

    public async Task<int> TestAsync(ImapSettings settings, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(settings, ct);
        var folder = await OpenAsync(client, settings.Folder, ct);
        var count = folder.Count;
        await client.DisconnectAsync(true, ct);
        return count;
    }

    public async Task<MailboxBatch> FetchAsync(ImapSettings settings, long afterUid, int max, int maxBytes, CancellationToken ct = default)
    {
        using var client = await ConnectAsync(settings, ct);
        var folder = await OpenAsync(client, settings.Folder, ct);
        var validity = (long)folder.UidValidity;
        long newest = afterUid;
        var messages = new List<ReceivedMail>();
        if (folder.Count > 0)
        {
            var start = afterUid >= uint.MaxValue - 1 ? uint.MaxValue : (uint)(afterUid + 1);
            var range = new UniqueIdRange(new UniqueId(Math.Max(1, start)), UniqueId.MaxValue);
            var summaries = await folder.FetchAsync(range, MessageSummaryItems.UniqueId | MessageSummaryItems.Size, ct);
            foreach (var summary in summaries.Where(s => s.UniqueId.Id > afterUid).OrderBy(s => s.UniqueId.Id))
            {
                if (max > 0 && messages.Count >= max)
                    break; // enough for this time: the rest are read on the next poll
                newest = Math.Max(newest, summary.UniqueId.Id);
                if (max == 0 || (summary.Size ?? 0) > maxBytes)
                    continue; // only noting where the folder ends, or too large to be worth downloading
                var message = await folder.GetMessageAsync(summary.UniqueId, ct);
                messages.Add(ToReceived(summary.UniqueId.Id, message));
            }
        }

        await client.DisconnectAsync(true, ct);
        return new MailboxBatch(messages, validity, newest);
    }

    /// <summary>The parts of a message the system keeps. HTML is reduced to its text; attachments are ignored.</summary>
    public static ReceivedMail ToReceived(long uid, MimeMessage message)
    {
        var from = message.From.Mailboxes.FirstOrDefault();
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(message.HtmlBody))
            text = HtmlSanitizer.ToPlainText(message.HtmlBody.Length > HtmlSanitizer.MaxInputLength ? message.HtmlBody[..HtmlSanitizer.MaxInputLength] : message.HtmlBody);
        var date = message.Date == DateTimeOffset.MinValue ? DateTime.UtcNow : message.Date.UtcDateTime;
        return new ReceivedMail(uid, message.MessageId, message.InReplyTo, from?.Address ?? string.Empty, from?.Name, message.Subject ?? string.Empty, date, text ?? string.Empty);
    }
}
