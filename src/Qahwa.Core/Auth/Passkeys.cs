using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Qahwa.Core.Auth;

/// <summary>Settings for passkeys (Qahwa:Auth:Passkeys).</summary>
public sealed class PasskeyOptions
{
    /// <summary>Whether people may register and sign in with passkeys.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The name an authenticator shows next to the passkey.</summary>
    public string RelyingPartyName { get; set; } = "Qahwa";

    /// <summary>
    /// The domain passkeys are bound to (<c>example.com</c>). Empty means the host of the site's address. A passkey made for one domain
    /// never works for another, which is what makes passkeys immune to look-alike sites.
    /// </summary>
    public string? RpId { get; set; }

    /// <summary>The addresses (<c>https://qahwa.example.com</c>) that may ask for a passkey; empty means the site's public address, else the address of the request.</summary>
    public string[] Origins { get; set; } = [];
}

/// <summary>Where passkeys are used: the domain they are bound to and the page addresses that may ask for them.</summary>
public sealed record RelyingParty(string Id, string Name, IReadOnlyList<string> Origins);

public sealed record PasskeyInfo(string Id, string Label, DateTime Created, DateTime? LastUsed);

public sealed record PasskeyRegistration(string? Token, string? Id, string? ClientDataJson, string? AttestationObject, string? Label);

public sealed record PasskeyAssertion(string? Token, string? Id, string? ClientDataJson, string? AuthenticatorData, string? Signature, string? UserHandle);

/// <summary>
/// Passkeys (WebAuthn): sign in with a fingerprint, face or device PIN instead of a password. The server keeps only a public key per
/// passkey; signing in is a signature over a one-time challenge, bound to this site's domain by the browser. A passkey sign-in needs
/// the authenticator to have checked the person (user verification), so it stands for both "something you have" and "something you
/// are/know" and no second code is asked. Attestation is not asked for (any authenticator is welcome) and only the two algorithms
/// every authenticator supports are accepted: ES256 (ECDSA P-256) and RS256 (RSA 2048 or more). A signature counter that does not
/// move forward (when the authenticator keeps one) is refused as a possible clone.
/// </summary>
public sealed class PasskeyService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    ISecretProtector protector,
    IOptions<PasskeyOptions> options,
    TimeProvider clock)
{
    public const string DocTypeName = "Passkey";
    public const int MaxPerUser = 10;
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, DateTime> _used = new(StringComparer.Ordinal);

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    public bool Enabled => options.Value.Enabled;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 100_000)
            return null;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static DocumentValidationException Invalid(string message) => new(message);

    /// <summary>The relying party for a site: from the settings, else the site's public address, else the address the request came to.</summary>
    public RelyingParty RelyingPartyFor(string? publicUrl, string requestOrigin)
    {
        var settings = options.Value;
        var origins = settings.Origins.Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0).ToList();
        if (origins.Count == 0)
            origins.Add(!string.IsNullOrWhiteSpace(publicUrl) ? publicUrl.Trim().TrimEnd('/') : requestOrigin);
        var host = Uri.TryCreate(origins[0], UriKind.Absolute, out var first) ? first.Host : "localhost";
        return new RelyingParty(string.IsNullOrWhiteSpace(settings.RpId) ? host : settings.RpId.Trim().ToLowerInvariant(), settings.RelyingPartyName, origins);
    }

    private static string UserHandle(string user) => Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes("qahwa-passkey|" + user)));

    private string Challenge(string kind, string user, out string challenge)
    {
        challenge = Base64Url(RandomNumberGenerator.GetBytes(32));
        return protector.Protect($"pk|{kind}|{user}|{(Now() + ChallengeLifetime).Ticks.ToString(CultureInfo.InvariantCulture)}|{challenge}");
    }

    /// <returns>The challenge the token carries, or null when the token is not ours, for another purpose or person, old or already used.</returns>
    private string? Redeem(string? token, string kind, string user)
    {
        var plain = token is null || token.Length > 2000 ? null : protector.Unprotect(token);
        var parts = plain?.Split('|');
        if (parts is not ["pk", var k, var who, var ticks, var challenge] || k != kind || who != user
            || !long.TryParse(ticks, NumberStyles.None, CultureInfo.InvariantCulture, out var expires) || expires < Now().Ticks)
            return null;
        var now = Now();
        foreach (var old in _used.Where(u => now - u.Value > ChallengeLifetime * 2).Select(u => u.Key).ToList())
            _used.TryRemove(old, out _);
        return _used.TryAdd(token!, now) ? challenge : null; // a challenge answers once
    }

    // ---- registering ----

    /// <summary>What the browser needs to make a passkey for this person (<c>navigator.credentials.create</c>).</summary>
    public async Task<object> BeginRegistrationAsync(string user, string displayName, RelyingParty rp, CancellationToken ct = default)
    {
        var existing = await ForUserAsync(user, ct);
        if (existing.Count >= MaxPerUser)
            throw Invalid($"You can have at most {MaxPerUser} passkeys. Remove one you no longer use.");
        var token = Challenge("reg", user, out var challenge);
        return new
        {
            token,
            publicKey = new
            {
                challenge,
                rp = new { id = rp.Id, name = rp.Name },
                user = new { id = UserHandle(user), name = user, displayName },
                pubKeyCredParams = new[] { new { type = "public-key", alg = -7 }, new { type = "public-key", alg = -257 } },
                excludeCredentials = existing.Select(r => new { type = "public-key", id = (string)r["credential_id"]! }).ToArray(),
                authenticatorSelection = new { residentKey = "preferred", userVerification = "preferred" },
                attestation = "none",
                timeout = 300_000,
            },
        };
    }

    public async Task<PasskeyInfo> FinishRegistrationAsync(string user, PasskeyRegistration request, RelyingParty rp, CancellationToken ct = default)
    {
        var refused = Invalid("The passkey could not be verified. Try again.");
        var expected = Redeem(request.Token, "reg", user) ?? throw Invalid("This request has expired. Start again.");
        var id = FromBase64Url(request.Id);
        var clientData = FromBase64Url(request.ClientDataJson);
        var attestation = FromBase64Url(request.AttestationObject);
        if (id is not { Length: > 0 and <= 1023 } || clientData is null || attestation is null || !ClientDataOk(clientData, "webauthn.create", expected, rp))
            throw refused;

        byte[] authData;
        try
        {
            authData = ((Dictionary<object, object>)Cbor.Decode(attestation))["authData"] as byte[] ?? throw refused;
        }
        catch (Exception e) when (e is InvalidDataException or KeyNotFoundException or InvalidCastException)
        {
            throw refused;
        }

        if (authData.Length < 55 || !authData.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Encoding.ASCII.GetBytes(rp.Id))) || (authData[32] & 0x01) == 0 || (authData[32] & 0x40) == 0)
            throw refused;
        var idLength = (authData[53] << 8) | authData[54];
        if (idLength != id.Length || authData.Length < 55 + idLength || !authData.AsSpan(55, idLength).SequenceEqual(id))
            throw refused;

        (int Alg, string PublicKey) key;
        try
        {
            key = ReadKey(Cbor.Decode(authData.AsSpan(55 + idLength).ToArray(), out _));
        }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or CryptographicException or InvalidCastException or KeyNotFoundException)
        {
            throw refused;
        }

        var credentialId = Base64Url(id);
        if (await repository.CountAsync(Meta, new ListQuery { Filters = [new("credential_id", FilterOp.Eq, credentialId)] }, ct) > 0)
            throw new ConflictException("This passkey is already registered.");
        if ((await ForUserAsync(user, ct)).Count >= MaxPerUser)
            throw Invalid($"You can have at most {MaxPerUser} passkeys. Remove one you no longer use.");

        var label = (request.Label ?? string.Empty).Trim();
        if (label.Length > 60 || label.Any(char.IsControl))
            throw Invalid("Keep the name to 60 characters of plain text.");
        var row = HistoryRecorder.Standard(user, Now());
        row["user"] = user;
        row["credential_id"] = credentialId;
        row["public_key"] = key.PublicKey;
        row["alg"] = (long)key.Alg;
        row["sign_count"] = (long)BigEndian(authData, 33);
        row["label"] = label.Length == 0 ? "Passkey" : label;
        await repository.InsertAsync(Meta, row, null, ct);
        return new PasskeyInfo((string)row["name"]!, (string)row["label"]!, (DateTime)row["creation"]!, null);
    }

    private static long BigEndian(byte[] data, int at) => ((long)data[at] << 24) | ((long)data[at + 1] << 16) | ((long)data[at + 2] << 8) | data[at + 3];

    /// <summary>The COSE key an authenticator made, as (algorithm, base64 SubjectPublicKeyInfo). Only ES256 and RS256 are accepted.</summary>
    private static (int Alg, string PublicKey) ReadKey(object cose)
    {
        var map = (Dictionary<object, object>)cose;
        long Number(long label) => Convert.ToInt64(map[label], CultureInfo.InvariantCulture);
        byte[] Bytes(long label) => (byte[])map[label];
        var kty = Number(1);
        var alg = Number(3);
        if (kty == 2 && alg == -7 && Number(-1) == 1)
        {
            var x = Bytes(-2);
            var y = Bytes(-3);
            if (x.Length != 32 || y.Length != 32)
                throw new ArgumentException("Bad point.");
            using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } }); // throws for a point not on the curve
            return (-7, Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        }

        if (kty == 3 && alg == -257)
        {
            var n = Bytes(-1);
            if (n.Length < 256 || n[0] == 0)
                throw new ArgumentException("Too small a key.");
            using var rsa = RSA.Create(new RSAParameters { Modulus = n, Exponent = Bytes(-2) });
            return (-257, Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()));
        }

        throw new ArgumentException("Unsupported key.");
    }

    private bool ClientDataOk(byte[] clientData, string type, string challenge, RelyingParty rp)
    {
        try
        {
            using var json = JsonDocument.Parse(clientData);
            var root = json.RootElement;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var origin = Text("origin");
            return Text("type") == type
                && Text("challenge") is { } got && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(got), Encoding.ASCII.GetBytes(challenge))
                && origin is not null && rp.Origins.Contains(origin, StringComparer.Ordinal)
                && !(root.TryGetProperty("crossOrigin", out var cross) && cross.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- signing in ----

    /// <summary>What the browser needs to ask for a passkey (<c>navigator.credentials.get</c>); with no email it is "whichever passkey the device has for this site".</summary>
    public object BeginAuthentication(RelyingParty rp)
    {
        var token = Challenge("auth", string.Empty, out var challenge);
        return new { token, publicKey = new { challenge, rpId = rp.Id, timeout = 300_000, userVerification = "required", allowCredentials = Array.Empty<object>() } };
    }

    /// <summary>The person a passkey answer proves, or null for any failure (the reason is deliberately not told).</summary>
    public async Task<string?> FinishAuthenticationAsync(PasskeyAssertion request, RelyingParty rp, CancellationToken ct = default)
    {
        var expected = Redeem(request.Token, "auth", string.Empty);
        var id = FromBase64Url(request.Id);
        var clientData = FromBase64Url(request.ClientDataJson);
        var authData = FromBase64Url(request.AuthenticatorData);
        var signature = FromBase64Url(request.Signature);
        if (expected is null || id is null || clientData is null || authData is not { Length: >= 37 } || signature is null || !ClientDataOk(clientData, "webauthn.get", expected, rp))
            return null;
        if (!authData.AsSpan(0, 32).SequenceEqual(SHA256.HashData(Encoding.ASCII.GetBytes(rp.Id))) || (authData[32] & 0x01) == 0 || (authData[32] & 0x04) == 0)
            return null; // present and verified (fingerprint, face or PIN)

        var rows = await repository.ListAsync(Meta, new ListQuery { Filters = [new("credential_id", FilterOp.Eq, Base64Url(id))], Limit = 1 }, ct);
        if (rows.Count == 0)
            return null;
        var row = rows[0];
        var user = (string)row["user"]!;
        if (FromBase64Url(request.UserHandle) is { Length: > 0 } handle && Base64Url(handle) != UserHandle(user))
            return null;

        var signed = authData.Concat(SHA256.HashData(clientData)).ToArray();
        try
        {
            var key = Convert.FromBase64String((string)row["public_key"]!);
            var valid = Convert.ToInt64(row["alg"], CultureInfo.InvariantCulture) switch
            {
                -7 => ECDsa.Create().Also(e => e.ImportSubjectPublicKeyInfo(key, out _)).Using(e => e.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
                -257 => RSA.Create().Also(r => r.ImportSubjectPublicKeyInfo(key, out _)).Using(r => r.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)),
                _ => false,
            };
            if (!valid)
                return null;
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return null;
        }

        // A counter that stands still or goes back means a copy of the key may be in use (zero on both sides: the authenticator keeps none).
        var stored = Convert.ToInt64(row["sign_count"] ?? 0L, CultureInfo.InvariantCulture);
        var count = BigEndian(authData, 33);
        if ((count != 0 || stored != 0) && count <= stored)
            return null;
        await repository.UpdateAsync(Meta, (string)row["name"]!, new Dictionary<string, object?> { ["sign_count"] = count, ["last_used"] = Now() }, null, null, ct);
        return user;
    }

    // ---- looking after them ----

    private async Task<IReadOnlyList<Dictionary<string, object?>>> ForUserAsync(string user, CancellationToken ct) =>
        await repository.ListAsync(Meta, new ListQuery { Filters = [new("user", FilterOp.Eq, user)], OrderBy = "creation", Descending = true, Limit = 100 }, ct);

    public async Task<IReadOnlyList<PasskeyInfo>> ListAsync(string user, CancellationToken ct = default) =>
        (await ForUserAsync(user, ct)).Select(r => new PasskeyInfo((string)r["name"]!, r["label"] as string ?? "Passkey", (DateTime)r["creation"]!, r["last_used"] as DateTime?)).ToList();

    /// <summary>Removes one of the person's own passkeys.</summary>
    public async Task RemoveAsync(string user, string id, CancellationToken ct = default)
    {
        var row = await repository.GetAsync(Meta, id, ct);
        if (row is null || !string.Equals(row["user"] as string, user, StringComparison.Ordinal))
            throw new NotFoundException("Passkey not found.");
        await repository.DeleteAsync(Meta, id, null, ct);
    }
}

file static class Fluent
{
    public static T Also<T>(this T value, Action<T> act)
    {
        act(value);
        return value;
    }

    public static bool Using<T>(this T value, Func<T, bool> use) where T : IDisposable
    {
        using (value)
            return use(value);
    }
}

/// <summary>A small CBOR reader: what WebAuthn needs (maps, arrays, integers, byte and text strings, booleans, null), nothing else.</summary>
public static class Cbor
{
    public static object Decode(byte[] data) => Decode(data, out _);

    /// <param name="read">How many bytes the first value took (a COSE key follows other data in an authenticator's output).</param>
    public static object Decode(byte[] data, out int read)
    {
        var at = 0;
        var value = Item(data, ref at, 0);
        read = at;
        return value;
    }

    private static object Item(byte[] data, ref int at, int depth)
    {
        if (depth > 8 || at >= data.Length)
            throw new InvalidDataException("Bad CBOR.");
        var initial = data[at++];
        var major = initial >> 5;
        var info = initial & 0x1f;
        if (major == 7)
            return info switch { 20 => false, 21 => true, 22 => null!, _ => throw new InvalidDataException("Unsupported simple value.") };
        var length = info switch
        {
            < 24 => (ulong)info,
            24 => Take(data, ref at, 1),
            25 => Take(data, ref at, 2),
            26 => Take(data, ref at, 4),
            27 => Take(data, ref at, 8),
            _ => throw new InvalidDataException("Indefinite lengths are not accepted."),
        };
        switch (major)
        {
            case 0:
                return checked((long)length);
            case 1:
                return -1 - checked((long)length);
            case 2:
            case 3:
                if (length > (ulong)(data.Length - at))
                    throw new InvalidDataException("Bad length.");
                var chunk = data.AsSpan(at, (int)length).ToArray();
                at += (int)length;
                return major == 2 ? chunk : Encoding.UTF8.GetString(chunk);
            case 4:
                if (length > 1000)
                    throw new InvalidDataException("Too long.");
                var list = new List<object>();
                for (ulong i = 0; i < length; i++)
                    list.Add(Item(data, ref at, depth + 1));
                return list;
            case 5:
                if (length > 1000)
                    throw new InvalidDataException("Too long.");
                var map = new Dictionary<object, object>();
                for (ulong i = 0; i < length; i++)
                {
                    var key = Item(data, ref at, depth + 1);
                    if (!map.TryAdd(key, Item(data, ref at, depth + 1)))
                        throw new InvalidDataException("Duplicate key.");
                }

                return map;
            default:
                throw new InvalidDataException("Unsupported CBOR type.");
        }
    }

    private static ulong Take(byte[] data, ref int at, int count)
    {
        if (count > data.Length - at)
            throw new InvalidDataException("Bad CBOR.");
        ulong value = 0;
        for (var i = 0; i < count; i++)
            value = (value << 8) | data[at++];
        return value;
    }
}
