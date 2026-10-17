using System.Net;
using System.Net.Mail;
using Qahwa.Core;

/// <summary>Reads the site's saved SMTP account for each send, so changes apply without a restart.</summary>
public sealed class SmtpEmailSender(EmailAccountService accounts) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        var settings = await accounts.GetAsync(ct);
        if (string.IsNullOrWhiteSpace(settings.Host))
            throw new DocumentValidationException("Configure and enable outgoing email in Email Account before sending messages.");
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseSsl, Timeout = 30_000 };
        if (!string.IsNullOrEmpty(settings.User))
            client.Credentials = new NetworkCredential(settings.User, settings.Password);
        using var mail = new MailMessage(settings.From, message.To, message.Subject, message.Body) { IsBodyHtml = false };
        await client.SendMailAsync(mail, ct);
    }
}
