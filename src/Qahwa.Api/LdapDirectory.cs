using System.DirectoryServices.Protocols;
using System.Net;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>
/// The real directory: LDAP over TLS (LDAPS or STARTTLS) with the server's certificate checked by the system, protocol version 3, a time
/// limit on every call and no referral chasing. Each call makes its own connection (sign-ins are rare and a bind changes who the
/// connection is).
/// </summary>
public sealed class LdapDirectory : ILdapDirectory
{
    private static LdapConnection Connect(LdapOptions o, NetworkCredential? credential)
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(o.Server.Trim(), o.Port), credential, credential is null ? AuthType.Anonymous : AuthType.Basic)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 60)),
        };
        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
        connection.SessionOptions.SecureSocketLayer = o.UseTls && !o.StartTls;
        if (o.StartTls)
            connection.SessionOptions.StartTransportLayerSecurity(null);
        return connection;
    }

    public Task<IReadOnlyList<LdapEntry>> SearchAsync(LdapOptions o, string filter, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<LdapEntry>>(() =>
        {
            var credential = string.IsNullOrEmpty(o.BindDn) ? null : new NetworkCredential(o.BindDn, o.BindPassword ?? string.Empty);
            using var connection = Connect(o, credential);
            connection.Bind();
            var request = new SearchRequest(o.BaseDn, filter, SearchScope.Subtree, o.EmailAttribute, o.NameAttribute) { SizeLimit = 2, TimeLimit = TimeSpan.FromSeconds(Math.Clamp(o.TimeoutSeconds, 1, 60)) };
            SearchResponse response;
            try
            {
                response = (SearchResponse)connection.SendRequest(request);
            }
            catch (DirectoryOperationException e) when (e.Response?.ResultCode == ResultCode.SizeLimitExceeded)
            {
                return [new LdapEntry("ambiguous", null, null), new LdapEntry("ambiguous", null, null)];
            }

            static string? First(SearchResultEntry entry, string attribute) =>
                entry.Attributes.Contains(attribute) && entry.Attributes[attribute].Count > 0 ? entry.Attributes[attribute][0] as string : null;

            return response.Entries.Cast<SearchResultEntry>().Take(2).Select(e => new LdapEntry(e.DistinguishedName, First(e, o.EmailAttribute), First(e, o.NameAttribute))).ToList();
        }, ct);

    public Task<bool> BindAsync(LdapOptions o, string dn, string password, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(dn))
                return false; // an empty password would be an anonymous bind, which many servers accept
            try
            {
                using var connection = Connect(o, new NetworkCredential(dn, password));
                connection.Bind();
                return true;
            }
            catch (LdapException)
            {
                return false;
            }
        }, ct);
}
