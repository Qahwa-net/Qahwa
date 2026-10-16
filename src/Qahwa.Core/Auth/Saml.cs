using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>One SAML 2.0 identity provider (ADFS, Entra ID, Okta, Keycloak, Shibboleth…), <c>Qahwa:Auth:Saml:Providers:&lt;name&gt;</c>.</summary>
public sealed class SamlProvider
{
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>The provider's entity id (its "Issuer"). Everything it sends must say this.</summary>
    public string IdpEntityId { get; set; } = string.Empty;

    /// <summary>Where people are sent to sign in (https).</summary>
    public string SsoUrl { get; set; } = string.Empty;

    /// <summary>The provider's signing certificate(s), base64 of the DER (or PEM). Only these keys are ever used to check a signature; keys inside a message are ignored.</summary>
    public string[] Certificates { get; set; } = [];

    /// <summary>This application's entity id. Empty: <c>&lt;PublicUrl&gt;/api/auth/saml/&lt;name&gt;/metadata</c>.</summary>
    public string? SpEntityId { get; set; }

    /// <summary>The attribute holding the email address. Empty: the usual ones, then the NameID when it is an email address.</summary>
    public string? EmailAttribute { get; set; }

    public string? NameAttribute { get; set; }

    public string[] AllowedDomains { get; set; } = [];

    public bool AutoCreate { get; set; }

    public string[] DefaultRoles { get; set; } = [];
}

public sealed class SamlOptions
{
    public Dictionary<string, SamlProvider> Providers { get; set; } = new(StringComparer.Ordinal);
}

public sealed record SamlStart(string Url, string State);

/// <summary>
/// Signing in with a SAML 2.0 provider (the browser is sent to the provider, which posts a signed response back). The response is only
/// believed when: it answers a request made here by this browser (<c>InResponseTo</c>, kept in an encrypted one-time cookie); its
/// signature checks out against a configured certificate (RSA with SHA-256 or stronger, over exactly the element that is then read,
/// with no other transforms, no duplicate ids and a single assertion, so a signed part cannot be swapped for an unsigned one); it comes
/// from the configured issuer, for this application, to this address, within its validity, and has not been used before. XML is read with
/// DTDs and external entities off. The result is a person known by the provider's id (see <see cref="ExternalIdentityResolver"/>).
/// </summary>
public sealed class SamlService(
    IOptions<SamlOptions> options,
    IIdentityStore identities,
    AuthService auth,
    ISecretProtector protector,
    TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);
    public const int MaxResponseBytes = 400_000;

    private const string Protocol = "urn:oasis:names:tc:SAML:2.0:protocol";
    private const string Assertion = "urn:oasis:names:tc:SAML:2.0:assertion";
    private const string Dsig = "http://www.w3.org/2000/09/xmldsig#";
    private const string BearerMethod = "urn:oasis:names:tc:SAML:2.0:cm:bearer";
    private const string StatusSuccess = "urn:oasis:names:tc:SAML:2.0:status:Success";
    private static readonly string[] SignatureMethods =
    [
        "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256", "http://www.w3.org/2001/04/xmldsig-more#rsa-sha384", "http://www.w3.org/2001/04/xmldsig-more#rsa-sha512",
    ];
    private static readonly string[] DigestMethods = ["http://www.w3.org/2001/04/xmlenc#sha256", "http://www.w3.org/2001/04/xmldsig-more#sha384", "http://www.w3.org/2001/04/xmlenc#sha512"];
    private static readonly string[] Canonicalizations = ["http://www.w3.org/2001/10/xml-exc-c14n#", "http://www.w3.org/2001/10/xml-exc-c14n#WithComments", "http://www.w3.org/TR/2001/REC-xml-c14n-20010315"];
    private static readonly string[] EmailAttributes =
    [
        "email", "mail", "emailaddress", "urn:oid:0.9.2342.19200300.100.1.3", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
        "http://schemas.xmlsoap.org/claims/EmailAddress", "urn:mace:dir:attribute-def:mail",
    ];
    private static readonly string[] NameAttributes =
    [
        "displayname", "name", "cn", "urn:oid:2.16.840.1.113730.3.1.241", "http://schemas.microsoft.com/identity/claims/displayname",
        "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name",
    ];

    private readonly ConcurrentDictionary<string, DateTime> _used = new(StringComparer.Ordinal);

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    public IReadOnlyList<(string Name, string DisplayName)> Providers() =>
        options.Value.Providers.Where(p => SsoService.ValidName(p.Key) && Usable(p.Value))
            .Select(p => (p.Key, string.IsNullOrWhiteSpace(p.Value.DisplayName) ? p.Key : p.Value.DisplayName)).OrderBy(p => p.Item2, StringComparer.OrdinalIgnoreCase).ToList();

    private static bool Usable(SamlProvider p) => p.IdpEntityId.Length > 0 && p.Certificates.Length > 0 && Uri.TryCreate(p.SsoUrl, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.UserInfo.Length == 0;

    /// <summary>The settings must make sense; checked when the server starts (a provider that cannot work is reported, not silently left out).</summary>
    public static string? Problem(SamlOptions o)
    {
        foreach (var (name, p) in o.Providers)
        {
            if (!SsoService.ValidName(name))
                return $"Qahwa:Auth:Saml:Providers:{name}: the name may only use letters, digits, dash and underscore.";
            if (!Usable(p))
                return $"Qahwa:Auth:Saml:Providers:{name} needs IdpEntityId, an https SsoUrl and at least one certificate.";
            if (p.AutoCreate && p.AllowedDomains.Length == 0)
                return $"Qahwa:Auth:Saml:Providers:{name}: AutoCreate needs AllowedDomains.";
            foreach (var certificate in p.Certificates)
                if (Certificate(certificate) is null)
                    return $"Qahwa:Auth:Saml:Providers:{name}: a certificate is not valid.";
        }

        return null;
    }

    private SamlProvider Provider(string name) =>
        SsoService.ValidName(name) && options.Value.Providers.TryGetValue(name, out var p) && Usable(p) ? p : throw new SsoException("unknown_provider", $"No provider '{name}'.");

    public static string EntityId(string name, string baseUrl, SamlProvider provider) =>
        string.IsNullOrWhiteSpace(provider.SpEntityId) ? $"{baseUrl.TrimEnd('/')}/api/auth/saml/{name}/metadata" : provider.SpEntityId;

    private static X509Certificate2? Certificate(string text)
    {
        try
        {
            var trimmed = text.Trim();
            if (trimmed.Contains("BEGIN CERTIFICATE", StringComparison.Ordinal))
                return X509Certificate2.CreateFromPem(trimmed);
            return X509CertificateLoader.LoadCertificate(Convert.FromBase64String(string.Concat(trimmed.Where(c => !char.IsWhiteSpace(c)))));
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    // ---- the way out ----

    /// <summary>Makes the request that sends the browser to the provider, and the one-time value this browser must bring back.</summary>
    public SamlStart Start(string providerName, string acsUrl, string spEntityId, string? next)
    {
        var provider = Provider(providerName);
        var id = "_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
        var relay = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var xml = $"<samlp:AuthnRequest xmlns:samlp=\"{Protocol}\" xmlns:saml=\"{Assertion}\" ID=\"{id}\" Version=\"2.0\" " +
                  $"IssueInstant=\"{Now().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}\" Destination=\"{Escape(provider.SsoUrl)}\" " +
                  $"ProtocolBinding=\"urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST\" AssertionConsumerServiceURL=\"{Escape(acsUrl)}\">" +
                  $"<saml:Issuer>{Escape(spEntityId)}</saml:Issuer><samlp:NameIDPolicy AllowCreate=\"true\"/></samlp:AuthnRequest>";
        using var packed = new MemoryStream();
        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(Encoding.UTF8.GetBytes(xml));
        var url = provider.SsoUrl + (provider.SsoUrl.Contains('?') ? "&" : "?")
            + $"SAMLRequest={Uri.EscapeDataString(Convert.ToBase64String(packed.ToArray()))}&RelayState={relay}";
        var payload = JsonSerializer.Serialize(new { p = providerName, i = id, r = relay, x = (Now() + Lifetime).Ticks, next = SsoService.SafeNext(next) });
        return new SamlStart(url, protector.Protect(payload));
    }

    private static string Escape(string text) => System.Security.SecurityElement.Escape(text) ?? string.Empty;

    private sealed record Saved(string P, string I, string R, long X, string Next);

    // ---- the way back ----

    public async Task<SsoSignIn> CompleteAsync(string providerName, string acsUrl, string spEntityId, string? savedState, string? samlResponse, string? relayState, CancellationToken ct = default)
    {
        var provider = Provider(providerName);
        Saved? saved = null;
        try
        {
            var plain = string.IsNullOrEmpty(savedState) || savedState.Length > 4000 ? null : protector.Unprotect(savedState);
            saved = plain is null ? null : JsonSerializer.Deserialize<Saved>(plain, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // treated as no saved state below
        }

        if (saved is null || saved.P != providerName || saved.X <= Now().Ticks)
            throw new SsoException("expired", "The sign-in took too long or did not start here.");
        if (string.IsNullOrEmpty(relayState) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(relayState), Encoding.UTF8.GetBytes(saved.R)))
            throw new SsoException("bad_state", "The relay state did not match.");

        var doc = Parse(samlResponse);
        var assertion = Validate(doc, provider, acsUrl, spEntityId, saved.I);

        var (subject, email, name) = Read(assertion, provider);
        var user = await new ExternalIdentityResolver(identities, auth, clock).ResolveAsync(
            new ExternalPolicy(provider.AllowedDomains, provider.AutoCreate, provider.DefaultRoles), provider.IdpEntityId, subject, email, emailVerified: true, name, ct);
        return new SsoSignIn(user, saved.Next, providerName);
    }

    private static XmlDocument Parse(string? encoded)
    {
        if (string.IsNullOrEmpty(encoded) || encoded.Length > MaxResponseBytes * 4 / 3 + 8)
            throw new SsoException("no_response", "No response.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new SsoException("bad_response", "Not base64.");
        }

        if (bytes.Length > MaxResponseBytes)
            throw new SsoException("bad_response", "Too large.");
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, MaxCharactersInDocument = MaxResponseBytes * 2 };
            using var reader = XmlReader.Create(new MemoryStream(bytes), settings);
            var doc = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            doc.Load(reader);
            return doc;
        }
        catch (XmlException)
        {
            throw new SsoException("bad_response", "Not valid XML.");
        }
    }

    /// <summary>Checks the response and returns the one assertion that can be believed.</summary>
    private XmlElement Validate(XmlDocument doc, SamlProvider provider, string acsUrl, string spEntityId, string requestId)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("samlp", Protocol);
        ns.AddNamespace("saml", Assertion);
        ns.AddNamespace("ds", Dsig);

        var response = doc.DocumentElement;
        if (response is not { LocalName: "Response", NamespaceURI: Protocol })
            throw new SsoException("bad_response", "Not a SAML response.");
        if (response.GetAttribute("InResponseTo") != requestId)
            throw new SsoException("bad_request_id", "The response is not for the request that was made here.");
        if (response.GetAttribute("Destination") is { Length: > 0 } destination && destination != acsUrl)
            throw new SsoException("bad_destination", "The response was meant for another address.");
        var status = response.SelectSingleNode("samlp:Status/samlp:StatusCode/@Value", ns)?.Value;
        if (status != StatusSuccess)
            throw new SsoException("provider_refused", "The provider did not say success.");
        if (doc.SelectNodes("//saml:EncryptedAssertion", ns)!.Count > 0)
            throw new SsoException("encrypted", "Encrypted assertions are not supported.");

        // Ids must be unique, or a signature over one element could be made to stand for another.
        var ids = doc.SelectNodes("//*[@ID]")!.Cast<XmlElement>().Select(e => e.GetAttribute("ID")).ToList();
        if (ids.Count != ids.Distinct(StringComparer.Ordinal).Count())
            throw new SsoException("bad_signature", "Duplicate ids.");

        var signed = new HashSet<XmlElement>();
        var signatures = doc.SelectNodes("//ds:Signature", ns)!.Cast<XmlElement>().ToList();
        if (signatures.Count is 0 or > 2)
            throw new SsoException("unsigned", "The response is not signed.");
        var certificates = provider.Certificates.Select(Certificate).Where(c => c is not null).Select(c => c!).ToList();
        foreach (var signature in signatures)
        {
            if (signature.ParentNode is not XmlElement target || (target != response && !(target.LocalName == "Assertion" && target.NamespaceURI == Assertion && target.ParentNode == response)))
                throw new SsoException("bad_signature", "A signature is somewhere it should not be.");
            if (!CheckSignature(doc, signature, target, certificates))
                throw new SsoException("bad_signature", "The signature is not valid.");
            signed.Add(target);
        }

        var assertions = response.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == "Assertion" && e.NamespaceURI == Assertion).ToList();
        if (assertions.Count != 1 || doc.SelectNodes("//saml:Assertion", ns)!.Count != 1)
            throw new SsoException("bad_response", "Exactly one assertion is needed.");
        var assertion = assertions[0];
        if (!signed.Contains(assertion) && !signed.Contains(response))
            throw new SsoException("unsigned", "The assertion is not signed.");

        foreach (var issuer in new[] { response.SelectSingleNode("saml:Issuer", ns), assertion.SelectSingleNode("saml:Issuer", ns) })
            if (issuer is not null && issuer.InnerText.Trim() != provider.IdpEntityId)
                throw new SsoException("bad_issuer", "The response comes from another provider.");
        if (assertion.SelectSingleNode("saml:Issuer", ns)?.InnerText.Trim() != provider.IdpEntityId)
            throw new SsoException("bad_issuer", "The assertion has no issuer or the wrong one.");

        var now = Now();
        var conditions = assertion.SelectSingleNode("saml:Conditions", ns) as XmlElement ?? throw new SsoException("bad_conditions", "No conditions.");
        if (Time(conditions, "NotBefore") is { } notBefore && notBefore - Skew > now)
            throw new SsoException("not_yet", "The assertion is not valid yet.");
        if (Time(conditions, "NotOnOrAfter") is not { } notOnOrAfter || notOnOrAfter + Skew <= now)
            throw new SsoException("expired_assertion", "The assertion has expired.");
        var audiences = conditions.SelectNodes("saml:AudienceRestriction/saml:Audience", ns)!.Cast<XmlElement>().Select(a => a.InnerText.Trim()).ToList();
        if (!audiences.Contains(spEntityId, StringComparer.Ordinal))
            throw new SsoException("bad_audience", "The assertion is for another application.");

        var confirmed = assertion.SelectNodes("saml:Subject/saml:SubjectConfirmation", ns)!.Cast<XmlElement>().Any(c =>
            c.GetAttribute("Method") == BearerMethod
            && c.SelectSingleNode("saml:SubjectConfirmationData", ns) is XmlElement data
            && data.GetAttribute("Recipient") == acsUrl
            && data.GetAttribute("InResponseTo") == requestId
            && Time(data, "NotOnOrAfter") is { } until && until + Skew > now
            && (Time(data, "NotBefore") is not { } from || from - Skew <= now));
        if (!confirmed)
            throw new SsoException("bad_subject", "The assertion is not confirmed for this response.");

        // An assertion works once.
        var assertionId = assertion.GetAttribute("ID");
        if (assertionId.Length == 0)
            throw new SsoException("bad_response", "The assertion has no id.");
        foreach (var old in _used.Where(u => u.Value < now).Select(u => u.Key).ToList())
            _used.TryRemove(old, out _);
        if (!_used.TryAdd(provider.IdpEntityId + "|" + assertionId, notOnOrAfter + Skew))
            throw new SsoException("replayed", "That assertion was already used.");
        return assertion;
    }

    private static DateTime? Time(XmlElement element, string attribute) =>
        DateTime.TryParse(element.GetAttribute(attribute), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    /// <summary>True when the signature is over exactly <paramref name="target"/> (by its id), uses a strong method, no extra transforms, and a configured certificate.</summary>
    private static bool CheckSignature(XmlDocument doc, XmlElement signature, XmlElement target, List<X509Certificate2> certificates)
    {
        try
        {
            var signedXml = new SignedXml(doc);
            signedXml.LoadXml(signature);
            if (!SignatureMethods.Contains(signedXml.SignatureMethod, StringComparer.Ordinal) || !Canonicalizations.Contains(signedXml.SignedInfo!.CanonicalizationMethod, StringComparer.Ordinal))
                return false;
            if (signedXml.SignedInfo.References.Count != 1 || signedXml.SignedInfo.References[0] is not Reference reference)
                return false;
            var id = target.GetAttribute("ID");
            if (id.Length == 0 || reference.Uri != "#" + id || !DigestMethods.Contains(reference.DigestMethod, StringComparer.Ordinal))
                return false;
            foreach (Transform transform in reference.TransformChain)
            {
                if (transform is not XmlDsigEnvelopedSignatureTransform && transform is not XmlDsigExcC14NTransform && transform is not XmlDsigC14NTransform)
                    return false;
            }

            foreach (var certificate in certificates)
            {
                using var key = certificate.GetRSAPublicKey();
                if (key is not null && signedXml.CheckSignature(key))
                    return true;
            }

            return false;
        }
        catch (Exception e) when (e is CryptographicException or XmlException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static (string Subject, string? Email, string? Name) Read(XmlElement assertion, SamlProvider provider)
    {
        var ns = new XmlNamespaceManager(assertion.OwnerDocument.NameTable);
        ns.AddNamespace("saml", Assertion);
        var nameId = assertion.SelectSingleNode("saml:Subject/saml:NameID", ns) as XmlElement;
        var subject = nameId?.InnerText.Trim();
        if (string.IsNullOrEmpty(subject) || subject.Length > 255)
            throw new SsoException("bad_response", "No subject.");

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (XmlElement attribute in assertion.SelectNodes("saml:AttributeStatement/saml:Attribute", ns)!)
        {
            var value = attribute.SelectSingleNode("saml:AttributeValue", ns)?.InnerText.Trim();
            if (value is { Length: > 0 and <= 500 })
            {
                attributes.TryAdd(attribute.GetAttribute("Name"), value);
                if (attribute.GetAttribute("FriendlyName") is { Length: > 0 } friendly)
                    attributes.TryAdd(friendly, value);
            }
        }

        string? Find(string? configured, string[] usual) =>
            (configured is { Length: > 0 } ? new[] { configured } : usual).Select(n => attributes.GetValueOrDefault(n)).FirstOrDefault(v => v is not null);
        var email = Find(provider.EmailAttribute, EmailAttributes);
        if (email is null && nameId?.GetAttribute("Format") == "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress")
            email = subject;
        return (subject, email, Find(provider.NameAttribute, NameAttributes));
    }
}
