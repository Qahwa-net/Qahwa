using System.Security.Cryptography;
using System.Text;

namespace Qahwa.Testing;

/// <summary>
/// A stand-in for a person's passkey device: makes the same bytes a browser hands to the server for <c>navigator.credentials.create</c>
/// and <c>.get</c> (client data, attestation object, authenticator data, signature), with each part open to tampering by the test.
/// </summary>
public sealed class FakeAuthenticator
{
    private readonly ECDsa? _ec;
    private readonly RSA? _rsa;

    public FakeAuthenticator(bool rsa = false)
    {
        if (rsa)
            _rsa = RSA.Create(2048);
        else
            _ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public byte[] CredentialId { get; } = RandomNumberGenerator.GetBytes(32);

    public uint Counter { get; set; }

    public static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string Id => B64(CredentialId);

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes($$"""{"type":"{{type}}","challenge":"{{challenge}}","origin":"{{origin}}","crossOrigin":false}""");

    private byte[] CoseKey()
    {
        if (_ec is not null)
        {
            var q = _ec.ExportParameters(false).Q;
            return Map((1L, 2L), (3L, -7L), (-1L, 1L), (-2L, q.X!), (-3L, q.Y!));
        }

        var p = _rsa!.ExportParameters(false);
        return Map((1L, 3L), (3L, -257L), (-1L, p.Modulus!), (-2L, p.Exponent!));
    }

    private static byte[] RpHash(string rpId) => SHA256.HashData(Encoding.ASCII.GetBytes(rpId));

    private static byte[] Counter32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    /// <summary>What a browser sends back when a passkey is made. <paramref name="rpId"/> and <paramref name="origin"/> are what this device believes it is talking to.</summary>
    public (string Id, string ClientDataJson, string AttestationObject) Create(string challenge, string rpId, string origin, string type = "webauthn.create")
    {
        var authData = RpHash(rpId).Concat(new byte[] { 0x45 }).Concat(Counter32(Counter)).Concat(new byte[16])
            .Concat(new[] { (byte)(CredentialId.Length >> 8), (byte)CredentialId.Length }).Concat(CredentialId).Concat(CoseKey()).ToArray();
        var attestation = Map(("fmt", "none"), ("attStmt", Map()), ("authData", authData));
        return (Id, B64(ClientData(type, challenge, origin)), B64(attestation));
    }

    /// <summary>What a browser sends back for a sign-in. The counter moves forward each time unless the test sets it.</summary>
    public (string Id, string ClientDataJson, string AuthenticatorData, string Signature, string UserHandle) Get(
        string challenge, string rpId, string origin, bool verified = true, bool tamper = false, string type = "webauthn.get", string? userHandle = null)
    {
        Counter++;
        var authData = RpHash(rpId).Concat(new[] { (byte)(verified ? 0x05 : 0x01) }).Concat(Counter32(Counter)).ToArray();
        var clientData = ClientData(type, challenge, origin);
        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        var signature = _ec is not null
            ? _ec.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)
            : _rsa!.SignData(signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (tamper)
            signature[^1] ^= 0xff;
        return (Id, B64(clientData), B64(authData), B64(signature), userHandle ?? string.Empty);
    }

    // ---- a tiny CBOR writer ----

    private static byte[] Head(int major, long value)
    {
        var m = (byte)(major << 5);
        if (value < 24)
            return [(byte)(m | value)];
        if (value < 256)
            return [(byte)(m | 24), (byte)value];
        if (value < 65536)
            return [(byte)(m | 25), (byte)(value >> 8), (byte)value];
        return [(byte)(m | 26), (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
    }

    private static byte[] Value(object value) => value switch
    {
        long n when n >= 0 => Head(0, n),
        long n => Head(1, -1 - n),
        string s => Head(3, Encoding.UTF8.GetByteCount(s)).Concat(Encoding.UTF8.GetBytes(s)).ToArray(),
        byte[] b => Head(2, b.Length).Concat(b).ToArray(),
        byte[][] raw => raw.SelectMany(x => x).ToArray(),
        _ => throw new ArgumentException("Unsupported."),
    };

    private static byte[] Map(params (object Key, object Value)[] entries) =>
        Head(5, entries.Length).Concat(entries.SelectMany(e => Value(e.Key).Concat(e.Value is byte[] inner && e.Key is "attStmt" ? inner : Value(e.Value)))).ToArray();
}
