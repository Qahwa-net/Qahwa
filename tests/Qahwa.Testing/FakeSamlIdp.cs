using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Xml;

namespace Qahwa.Testing;

/// <summary>What a test wants to be different in a SAML response (everything defaults to a correct, signed assertion).</summary>
public sealed class SamlResponseOptions
{
    public string? Email { get; set; } = "ann@example.com";
    public string? Name { get; set; } = "Ann Lee";
    public string Issuer { get; set; } = FakeSamlIdp.EntityId;
    public string Audience { get; set; } = "https://qahwa.example.com/sp";
    public string? Destination { get; set; }
    public string? Recipient { get; set; }
    public string? InResponseTo { get; set; }
    public bool SignAssertion { get; set; } = true;
    public bool SignResponse { get; set; }
    public RSA? SignWith { get; set; }
    public string SignatureMethod { get; set; } = "http://www.w3.org/2001/04/xmldsig-more#rsa-sha256";
    public string DigestMethod { get; set; } = "http://www.w3.org/2001/04/xmlenc#sha256";
    public DateTime? NotBefore { get; set; }
    public DateTime? NotOnOrAfter { get; set; }
    public bool Success { get; set; } = true;
    public string AssertionId { get; set; } = "_a" + Guid.NewGuid().ToString("N");
    public string? Dtd { get; set; }
    public bool Encrypted { get; set; }
    public bool UseNameIdEmail { get; set; }

    /// <summary>Signature wrapping: an unsigned assertion for someone else placed before the signed one.</summary>
    public string? EvilSubjectFirst { get; set; }

    /// <summary>Change the signed XML after signing (for example swap the email).</summary>
    public Func<string, string>? TamperAfterSigning { get; set; }
}

/// <summary>A stand-in SAML identity provider that makes (and signs) responses.</summary>
public sealed class FakeSamlIdp
{
    public const string EntityId = "https://idp.example.com/saml";
    public const string SsoUrl = "https://idp.example.com/sso";

    public RSA Key { get; } = RSA.Create(2048);
    public X509Certificate2 Certificate { get; }

    public FakeSamlIdp()
    {
        var request = new CertificateRequest("CN=idp.example.com", Key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
    }

    public string CertificateText => Convert.ToBase64String(Certificate.Export(X509ContentType.Cert));

    /// <summary>The id of the request in the address the browser is sent to.</summary>
    public static string RequestId(string redirectUrl)
    {
        var query = redirectUrl[(redirectUrl.IndexOf("SAMLRequest=", StringComparison.Ordinal) + 12)..].Split('&')[0];
        using var inflate = new DeflateStream(new MemoryStream(Convert.FromBase64String(Uri.UnescapeDataString(query))), CompressionMode.Decompress);
        using var reader = new StreamReader(inflate);
        var xml = reader.ReadToEnd();
        var at = xml.IndexOf("ID=\"", StringComparison.Ordinal) + 4;
        return xml[at..xml.IndexOf('"', at)];
    }

    public static string RelayState(string redirectUrl) => redirectUrl[(redirectUrl.IndexOf("RelayState=", StringComparison.Ordinal) + 11)..].Split('&')[0];

    public string Response(string requestId, string acsUrl, string subject, SamlResponseOptions? options = null, DateTime? now = null)
    {
        var o = options ?? new SamlResponseOptions();
        var at = now ?? DateTime.UtcNow;
        string Iso(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var notBefore = o.NotBefore ?? at.AddMinutes(-1);
        var notOnOrAfter = o.NotOnOrAfter ?? at.AddMinutes(5);
        var inResponseTo = o.InResponseTo ?? requestId;
        var destination = o.Destination ?? acsUrl;
        var recipient = o.Recipient ?? acsUrl;

        string Assertion(string id, string who, string? email) =>
            $"<saml:Assertion xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"{id}\" Version=\"2.0\" IssueInstant=\"{Iso(at)}\">" +
            $"<saml:Issuer>{o.Issuer}</saml:Issuer>" +
            $"<saml:Subject><saml:NameID Format=\"{(o.UseNameIdEmail ? "urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress" : "urn:oasis:names:tc:SAML:2.0:nameid-format:persistent")}\">{who}</saml:NameID>" +
            $"<saml:SubjectConfirmation Method=\"urn:oasis:names:tc:SAML:2.0:cm:bearer\"><saml:SubjectConfirmationData NotOnOrAfter=\"{Iso(notOnOrAfter)}\" Recipient=\"{recipient}\" InResponseTo=\"{inResponseTo}\"/></saml:SubjectConfirmation></saml:Subject>" +
            $"<saml:Conditions NotBefore=\"{Iso(notBefore)}\" NotOnOrAfter=\"{Iso(notOnOrAfter)}\"><saml:AudienceRestriction><saml:Audience>{o.Audience}</saml:Audience></saml:AudienceRestriction></saml:Conditions>" +
            "<saml:AuthnStatement AuthnInstant=\"" + Iso(at) + "\"><saml:AuthnContext><saml:AuthnContextClassRef>urn:oasis:names:tc:SAML:2.0:ac:classes:Password</saml:AuthnContextClassRef></saml:AuthnContext></saml:AuthnStatement>" +
            "<saml:AttributeStatement>" +
            (email is null ? string.Empty : $"<saml:Attribute Name=\"email\"><saml:AttributeValue>{email}</saml:AttributeValue></saml:Attribute>") +
            (o.Name is null ? string.Empty : $"<saml:Attribute Name=\"displayName\"><saml:AttributeValue>{o.Name}</saml:AttributeValue></saml:Attribute>") +
            "</saml:AttributeStatement></saml:Assertion>";

        var body = (o.Dtd ?? string.Empty) +
            $"<samlp:Response xmlns:samlp=\"urn:oasis:names:tc:SAML:2.0:protocol\" xmlns:saml=\"urn:oasis:names:tc:SAML:2.0:assertion\" ID=\"_r{Guid.NewGuid():N}\" Version=\"2.0\" IssueInstant=\"{Iso(at)}\" Destination=\"{destination}\" InResponseTo=\"{inResponseTo}\">" +
            $"<saml:Issuer>{o.Issuer}</saml:Issuer>" +
            $"<samlp:Status><samlp:StatusCode Value=\"{(o.Success ? "urn:oasis:names:tc:SAML:2.0:status:Success" : "urn:oasis:names:tc:SAML:2.0:status:Responder")}\"/></samlp:Status>" +
            (o.EvilSubjectFirst is null ? string.Empty : Assertion("_evil", o.EvilSubjectFirst, o.EvilSubjectFirst)) +
            (o.Encrypted ? "<saml:EncryptedAssertion>x</saml:EncryptedAssertion>" : string.Empty) +
            Assertion(o.AssertionId, subject, o.UseNameIdEmail ? null : o.Email) +
            "</samlp:Response>";

        var doc = new XmlDocument { PreserveWhitespace = true };
        doc.LoadXml(o.Dtd is null ? body : body); // a DTD is part of the text on purpose
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("saml", "urn:oasis:names:tc:SAML:2.0:assertion");
        if (o.SignAssertion)
            Sign(doc, (XmlElement)doc.SelectSingleNode($"//saml:Assertion[@ID='{o.AssertionId}']", ns)!, o);
        if (o.SignResponse)
            Sign(doc, doc.DocumentElement!, o);

        var xml = doc.OuterXml;
        if (o.TamperAfterSigning is not null)
            xml = o.TamperAfterSigning(xml);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(xml));
    }

    private void Sign(XmlDocument doc, XmlElement target, SamlResponseOptions o)
    {
        var signed = new SignedXml(doc) { SigningKey = o.SignWith ?? Key };
        signed.SignedInfo!.SignatureMethod = o.SignatureMethod;
        var reference = new Reference("#" + target.GetAttribute("ID")) { DigestMethod = o.DigestMethod };
        reference.AddTransform(new XmlDsigEnvelopedSignatureTransform());
        reference.AddTransform(new XmlDsigExcC14NTransform());
        signed.AddReference(reference);
        signed.ComputeSignature();
        target.InsertAfter(doc.ImportNode(signed.GetXml(), true), target.FirstChild);
    }
}
