using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MimeKit;
using Qahwa.Hosting;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Api.Tests;

/// <summary>
/// Reads a real IMAP server (GreenMail in a container: <c>docker run -p 3025:3025 -p 3993:3993 -e GREENMAIL_OPTS="-Dgreenmail.setup.test.smtp
/// -Dgreenmail.setup.test.imaps -Dgreenmail.hostname=0.0.0.0 -Dgreenmail.users=desk:deskpass@example.com" greenmail/standalone:2.1.0</c>), with
/// <c>QAHWA_TEST_IMAP=localhost</c> set. Skipped otherwise.
/// </summary>
public class RealMailboxTests
{
    /// <summary>The test server's certificate is self-signed; only this test class accepts it.</summary>
    private sealed class TrustingMailbox : MailKitMailbox
    {
        protected override void Configure(ImapClient client)
        {
            client.CheckCertificateRevocation = false;
            client.ServerCertificateValidationCallback = (_, _, _, _) => true;
        }
    }

    private static async Task SendAsync(string host, string subject, string body, string from = "ann@example.com")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ann", from));
        message.To.Add(new MailboxAddress("Desk", "desk@example.com"));
        message.Subject = subject;
        message.Body = new TextPart("plain") { Text = body };
        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(host, 3025, MailKit.Security.SecureSocketOptions.None);
        await smtp.SendAsync(message);
        await smtp.DisconnectAsync(true);
    }

    [SkippableFact]
    public async Task A_real_IMAP_server_is_read_over_TLS_and_new_mail_becomes_Communications()
    {
        var host = Environment.GetEnvironmentVariable("QAHWA_TEST_IMAP");
        Skip.If(string.IsNullOrEmpty(host), "Set QAHWA_TEST_IMAP=localhost with a GreenMail container running to run this.");
        var engine = new TestEngine();
        var mailbox = new TrustingMailbox();
        var service = new Qahwa.Core.IncomingMailService(engine.Registry, engine.Repository, engine.EmailAccounts, mailbox, engine.History);
        await engine.Documents.UpdateAsync("Email Account", "Email Account", Values(
            ("enabled", false), ("imap_enabled", true), ("imap_server", host), ("imap_port", 3993), ("imap_username", "desk"),
            ("imap_password", "deskpass"), ("imap_folder", "INBOX")), TestUsers.Admin);

        await SendAsync(host!, "Already there", "old mail");
        var baseline = await service.TestAsync(); // connects over TLS, signs in, reports the folder size (the server may hold mail from earlier runs)
        Assert.True(baseline >= 1);
        Assert.Equal(0, await service.PollAsync()); // the first poll only notes where the folder ends

        await SendAsync(host!, "Hello <script>alert(1)</script>", "First line\r\nSecond line");
        await SendAsync(host!, "Second", "Another");
        Assert.Equal(2, await service.PollAsync());
        Assert.Equal(0, await service.PollAsync());

        var kept = (await engine.Documents.ListAsync("Communication", new Qahwa.Core.ListRequest { Fields = ["sender", "subject", "content", "message_id"], OrderBy = "creation", Descending = false, Limit = 10 }, TestUsers.Admin)).Data;
        Assert.Equal(2, kept.Count);
        Assert.All(kept, row => Assert.Equal("ann@example.com", row["sender"]));
        Assert.Contains(kept, row => ((string)row["content"]!).Contains("Second line", StringComparison.Ordinal));
        Assert.DoesNotContain(kept, row => ((string)row["subject"]!).Contains("Already there", StringComparison.Ordinal));

        // The mailbox is only read: the messages are still there.
        Assert.Equal(baseline + 2, await service.TestAsync());
    }

    [SkippableFact]
    public async Task A_server_that_is_not_there_is_an_error()
    {
        var host = Environment.GetEnvironmentVariable("QAHWA_TEST_IMAP");
        Skip.If(string.IsNullOrEmpty(host), "Set QAHWA_TEST_IMAP=localhost with a GreenMail container running to run this.");
        var mailbox = new TrustingMailbox();
        await Assert.ThrowsAnyAsync<Exception>(() => mailbox.TestAsync(new Qahwa.Core.ImapSettings(host!, 3999, false, "desk", "secret-pw-123", "INBOX")));
    }
}
