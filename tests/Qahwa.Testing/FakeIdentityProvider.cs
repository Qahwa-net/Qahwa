using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Qahwa.Core.Auth;

namespace Qahwa.Testing;

/// <summary>A stand-in OpenID Connect provider: its own RSA key, discovery, key set and token answer, and signed tokens on demand.</summary>
public sealed class FakeIdentityProvider : IOidcBackchannel
{
    public const string Authority = "https://idp.example.com";
    public const string ClientId = "qahwa-client";

    private RSA _rsa = RSA.Create(2048);
    private string _kid = "key-1";
    private RSA? _previous;
    private string? _previousKid;

    public string? NextToken { get; set; }

    public Dictionary<string, string>? LastForm { get; private set; }

    public string? LastClientSecret { get; private set; }

    public int KeyFetches { get; private set; }

    public OidcMetadata Metadata { get; set; } = new(Authority, Authority + "/authorize", Authority + "/token", Authority + "/keys");

    /// <summary>The provider changes its key, but anyone who looked a moment ago still has the old key set until they ask again.</summary>
    public void Rotate()
    {
        _previous = _rsa;
        _previousKid = _kid;
        _rsa = RSA.Create(2048);
        _kid = "key-2";
    }

    public string Issue(
        string subject, string? email = null, bool? verified = true, string? name = null, string? nonce = null, string audience = ClientId, string issuer = Authority,
        TimeSpan? lifetime = null, RSA? signWith = null, string? algorithm = null, DateTime? now = null, string? groupsClaim = null, object? groups = null)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject };
        if (email is not null)
            claims["email"] = email;
        if (verified is not null)
            claims["email_verified"] = verified.Value;
        if (name is not null)
            claims["name"] = name;
        if (nonce is not null)
            claims["nonce"] = nonce;
        if (groupsClaim is not null && groups is not null)
            claims[groupsClaim] = groups;
        var issued = now ?? DateTime.UtcNow;
        var key = new RsaSecurityKey(signWith ?? _rsa) { KeyId = signWith is null ? _kid : "other" };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims, IssuedAt = issued, NotBefore = issued.AddMinutes(-1), Expires = issued.Add(lifetime ?? TimeSpan.FromMinutes(5)),
            SigningCredentials = new SigningCredentials(key, algorithm ?? SecurityAlgorithms.RsaSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public Task<OidcMetadata> DiscoverAsync(string authority, CancellationToken ct = default) => Task.FromResult(Metadata);

    private static JsonWebKey Public(RSA rsa, string kid)
    {
        var key = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = kid });
        key.Use = "sig";
        return key;
    }

    public Task<JsonWebKeySet> KeysAsync(string jwksUri, bool refresh, CancellationToken ct = default)
    {
        KeyFetches++;
        var set = new JsonWebKeySet();
        if (refresh || _previous is null)
            set.Keys.Add(Public(_rsa, _kid));
        else
            set.Keys.Add(Public(_previous, _previousKid!)); // a stale copy: the old key only
        return Task.FromResult(set);
    }

    public Task<string?> ExchangeAsync(string tokenEndpoint, IReadOnlyDictionary<string, string> form, string? clientId, string? clientSecret, CancellationToken ct = default)
    {
        LastForm = new Dictionary<string, string>(form);
        LastClientSecret = clientSecret;
        return Task.FromResult(NextToken);
    }
}

public sealed class InMemoryIdentityStore : IIdentityStore
{
    private readonly Dictionary<(string, string), string> _rows = [];
    private readonly object _gate = new();

    public Task<string?> FindUserAsync(string issuer, string subject, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.TryGetValue((issuer, subject), out var user) ? user : null);
    }

    public Task<IReadOnlyList<string>> SubjectsOfAsync(string issuer, string user, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult<IReadOnlyList<string>>(_rows.Where(r => r.Key.Item1 == issuer && r.Value == user).Select(r => r.Key.Item2).ToList());
    }

    public Task<bool> LinkAsync(string issuer, string subject, string user, DateTime now, CancellationToken ct = default)
    {
        lock (_gate)
            return Task.FromResult(_rows.TryAdd((issuer, subject), user));
    }
}
