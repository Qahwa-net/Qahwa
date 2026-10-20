using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

/// <summary>Fallback transport from host configuration; a saved Email Account takes precedence.</summary>
public sealed class EmailSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "noreply@localhost.invalid";
}

public sealed class EmailAccountService(IMetaRegistry registry, IDocumentRepository repository, ISecretProtector secrets, EmailSettings fallback)
{
    public const string DocTypeName = "Email Account";

    public async Task<EmailSettings> GetAsync(CancellationToken ct = default)
    {
        var row = await repository.GetAsync(registry.Get(DocTypeName), DocTypeName, ct);
        if (row is null) return fallback;
        if (row.GetValueOrDefault("enabled") is not true) return new EmailSettings();
        var stored = row.GetValueOrDefault("smtp_password") as string;
        var password = string.IsNullOrEmpty(stored) ? null : secrets.Unprotect(stored);
        if (!string.IsNullOrEmpty(stored) && password is null)
            throw new DocumentValidationException("The email password cannot be decrypted. Enter it again in Email Account.");
        return new EmailSettings
        {
            Host = row.GetValueOrDefault("smtp_server") as string,
            Port = Convert.ToInt32(row.GetValueOrDefault("smtp_port") ?? 587),
            UseSsl = row.GetValueOrDefault("use_tls") is true,
            User = row.GetValueOrDefault("smtp_username") as string,
            Password = password,
            From = row.GetValueOrDefault("email_address") as string ?? string.Empty,
        };
    }

    /// <summary>The mailbox to read, or null when incoming mail is off or not complete. The connection is always encrypted.</summary>
    public async Task<ImapSettings?> GetIncomingAsync(CancellationToken ct = default)
    {
        var row = await repository.GetAsync(registry.Get(DocTypeName), DocTypeName, ct);
        if (row is null || row.GetValueOrDefault("imap_enabled") is not true)
            return null;
        var host = (row.GetValueOrDefault("imap_server") as string)?.Trim();
        var user = (row.GetValueOrDefault("imap_username") as string)?.Trim();
        var stored = row.GetValueOrDefault("imap_password") as string;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(stored))
            return null;
        var password = secrets.Unprotect(stored)
            ?? throw new DocumentValidationException("The mailbox password cannot be decrypted. Enter it again in Email Account.");
        var port = Convert.ToInt32(row.GetValueOrDefault("imap_port") ?? 993, System.Globalization.CultureInfo.InvariantCulture);
        var folder = (row.GetValueOrDefault("imap_folder") as string)?.Trim();
        return new ImapSettings(host, port is >= 1 and <= 65535 ? port : 993, row.GetValueOrDefault("imap_starttls") is true, user, password, string.IsNullOrEmpty(folder) ? "INBOX" : folder);
    }

    /// <summary>Where reading stopped: the number of the last message kept, and the folder version it belongs to.</summary>
    public async Task<(long LastUid, long? UidValidity)> GetCursorAsync(CancellationToken ct = default)
    {
        var row = await repository.GetAsync(registry.Get(DocTypeName), DocTypeName, ct);
        var validity = long.TryParse(row?.GetValueOrDefault("imap_uid_validity") as string, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (long?)null;
        return (Convert.ToInt64(row?.GetValueOrDefault("imap_last_uid") ?? 0L, System.Globalization.CultureInfo.InvariantCulture), validity);
    }

    /// <summary>Remembers where reading stopped. It does not touch the version of the Email Account, so a person saving the form is not made to reload.</summary>
    public async Task SetCursorAsync(long lastUid, long uidValidity, CancellationToken ct = default) =>
        await repository.UpdateAsync(registry.Get(DocTypeName), DocTypeName, new Dictionary<string, object?>
        {
            ["imap_last_uid"] = lastUid, ["imap_uid_validity"] = uidValidity.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, null, null, ct);

    public async Task<bool> IsConfiguredAsync(CancellationToken ct = default) =>
        !string.IsNullOrWhiteSpace((await GetAsync(ct)).Host);

    public async Task<string?> PublicUrlAsync(CancellationToken ct = default)
    {
        var row = await repository.GetAsync(registry.Get(DocTypeName), DocTypeName, ct);
        var value = row?.GetValueOrDefault("site_url") as string;
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new DocumentValidationException("Email Account site URL must be an absolute HTTP or HTTPS URL without credentials, a query or fragment.");
        return value.TrimEnd('/');
    }
}
