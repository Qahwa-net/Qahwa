using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>Sign-in against a company directory (LDAP / Active Directory), <c>Qahwa:Auth:Ldap</c>. Off unless <see cref="Enabled"/> is set.</summary>
public sealed class LdapOptions
{
    public bool Enabled { get; set; }

    public string Server { get; set; } = string.Empty;

    /// <summary>636 for LDAPS (the default); 389 with <see cref="StartTls"/>.</summary>
    public int Port { get; set; } = 636;

    /// <summary>Use LDAPS (TLS from the start). Plain LDAP is refused unless the server is this machine.</summary>
    public bool UseTls { get; set; } = true;

    /// <summary>Upgrade a plain connection with STARTTLS instead of LDAPS.</summary>
    public bool StartTls { get; set; }

    /// <summary>A read-only account used to find people. Leave empty only if the directory allows anonymous searches.</summary>
    public string? BindDn { get; set; }

    public string? BindPassword { get; set; }

    public string BaseDn { get; set; } = string.Empty;

    /// <summary>Finds a person by the email they typed; <c>{0}</c> is replaced by the (escaped) email.</summary>
    public string UserFilter { get; set; } = "(&(objectClass=person)(mail={0}))";

    public string EmailAttribute { get; set; } = "mail";

    public string NameAttribute { get; set; } = "displayName";

    public string[] AllowedDomains { get; set; } = [];

    public bool AutoCreate { get; set; }

    public string[] DefaultRoles { get; set; } = [];

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>A person found in the directory.</summary>
public sealed record LdapEntry(string Dn, string? Email, string? FullName);

/// <summary>Talks to the directory. The real one uses LDAP over TLS; tests use a stand-in.</summary>
public interface ILdapDirectory
{
    /// <summary>Looks people up with the service account. Returns at most two entries (more than one is ambiguous).</summary>
    Task<IReadOnlyList<LdapEntry>> SearchAsync(LdapOptions options, string filter, CancellationToken ct = default);

    /// <summary>True when the directory accepts this password for this person.</summary>
    Task<bool> BindAsync(LdapOptions options, string dn, string password, CancellationToken ct = default);
}

/// <summary>
/// Signing in with a directory password. The person types their email and directory password; the service finds their entry with
/// a read-only account (the email is escaped into the filter, so it cannot change the search), then asks the directory to check the
/// password for that entry. A password is never sent to the directory empty (an empty password makes many servers treat the bind as
/// anonymous and succeed), nothing is stored of it, and the account gets a random local password nobody knows. People who sign in this
/// way are marked in the identity table and can then only use the directory, so removing them there ends their access here.
/// </summary>
public sealed class LdapService(IOptions<LdapOptions> options, ILdapDirectory directory, IIdentityStore identities, AuthService auth, TimeProvider clock)
{
    public bool Enabled => options.Value.Enabled;

    private string Issuer => $"ldap://{options.Value.Server.Trim().ToLowerInvariant()}";

    /// <summary>The settings must make sense and be safe; checked when the server starts.</summary>
    public static string? Problem(LdapOptions o)
    {
        if (!o.Enabled)
            return null;
        if (string.IsNullOrWhiteSpace(o.Server) || string.IsNullOrWhiteSpace(o.BaseDn))
            return "Qahwa:Auth:Ldap needs Server and BaseDn.";
        if (!o.UserFilter.Contains("{0}", StringComparison.Ordinal))
            return "Qahwa:Auth:Ldap:UserFilter must contain {0}.";
        if (!o.UseTls && !o.StartTls && !IsThisMachine(o.Server))
            return "Qahwa:Auth:Ldap sends passwords: use LDAPS (UseTls) or StartTls, or a server on this machine.";
        if (o.AutoCreate && o.AllowedDomains.Length == 0)
            return "Qahwa:Auth:Ldap:AutoCreate needs AllowedDomains.";
        return null;
    }

    private static bool IsThisMachine(string server) => server.Trim().ToLowerInvariant() is "localhost" or "127.0.0.1" or "::1" or "[::1]";

    /// <summary>Escapes text for use inside an LDAP search filter (RFC 4515).</summary>
    public static string EscapeFilter(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>True for people who sign in through the directory (they have signed in that way before).</summary>
    public async Task<bool> IsDirectoryUserAsync(string key, CancellationToken ct = default) =>
        Enabled && (await identities.SubjectsOfAsync(Issuer, key, ct)).Count > 0;

    /// <returns>The person's email key when the directory accepts them, or null for any failure (the reason is never told).</returns>
    public async Task<string?> SignInAsync(string? email, string? password, CancellationToken ct = default)
    {
        var o = options.Value;
        var key = AuthService.Normalize(email);
        if (!o.Enabled || key is null || !EmailService.ValidAddress(key) || string.IsNullOrEmpty(password) || password.Length > AuthService.MaxPasswordLength || password.Contains('\0'))
            return null;

        var domain = key[(key.LastIndexOf('@') + 1)..];
        if (o.AllowedDomains.Length > 0 && !o.AllowedDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            return null;

        var found = await directory.SearchAsync(o, o.UserFilter.Replace("{0}", EscapeFilter(key), StringComparison.Ordinal), ct);
        if (found.Count != 1 || string.IsNullOrWhiteSpace(found[0].Dn) || !string.Equals(AuthService.Normalize(found[0].Email), key, StringComparison.Ordinal))
            return null; // nobody, or more than one: not a sign-in

        var entry = found[0];
        if (!await directory.BindAsync(o, entry.Dn, password, ct))
            return null;

        // Someone already linked: the directory entry decides who they are, not the email.
        var user = await identities.FindUserAsync(Issuer, entry.Dn, ct) ?? key;
        var state = await auth.AccountStateAsync(user, ct);
        if (state == AccountState.Disabled)
            return null; // a directory never switches an account back on
        if (state is AccountState.Active or AccountState.NoPassword)
        {
            if ((await identities.SubjectsOfAsync(Issuer, user, ct)).Any(s => s != entry.Dn))
                return null; // this account belongs to another entry
            if (state == AccountState.NoPassword)
                await auth.SetPasswordAsync(user, RandomPassword(), ct);
        }
        else if (o.AutoCreate && o.AllowedDomains.Length > 0)
        {
            var name = string.IsNullOrWhiteSpace(entry.FullName) ? key[..key.LastIndexOf('@')] : entry.FullName.Trim();
            await auth.EnsureUserAsync(key, name.Length > 140 ? name[..140] : name, RandomPassword(), o.DefaultRoles, ct);
        }
        else
        {
            return null;
        }

        var utc = clock.GetUtcNow().UtcDateTime;
        if (!await identities.LinkAsync(Issuer, entry.Dn, user, new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc), ct) && await identities.FindUserAsync(Issuer, entry.Dn, ct) != user)
            return null;
        return user;
    }

    private static string RandomPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
