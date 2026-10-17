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
