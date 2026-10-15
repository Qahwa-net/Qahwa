using System.Net;
using System.Net.Mail;
using Qahwa.Core;

/// <summary>Settings under <c>Qahwa:Email</c>. With no <c>Host</c> nothing leaves the machine: messages are written to the log.</summary>
public sealed class EmailSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "noreply@localhost.invalid";
}

/// <summary>Sends through an SMTP server. Plain text only; subject and addresses were validated when queued.</summary>
public sealed class SmtpEmailSender(EmailSettings settings) : IEmailSender
{
    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.UseSsl, Timeout = 30_000 };
        if (!string.IsNullOrEmpty(settings.User))
            client.Credentials = new NetworkCredential(settings.User, settings.Password);
        using var mail = new MailMessage(settings.From, message.To, message.Subject, message.Body) { IsBodyHtml = false };
        await client.SendMailAsync(mail, ct);
    }
}

/// <summary>Development stand-in: says what it would have sent. No message leaves the machine.</summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        logger.LogInformation("Email (not sent, no SMTP host configured) to {To}: {Subject}", message.To, message.Subject);
        return Task.CompletedTask;
    }
}
