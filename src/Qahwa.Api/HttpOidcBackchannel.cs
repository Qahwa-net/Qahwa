using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>
/// The real way to talk to an identity provider: HTTPS only (plain http only for localhost, for development), a short time limit, no
/// redirects followed, answers limited to 1 MB, and discovery and keys remembered for a few minutes. The addresses it calls come from the
/// provider's own settings, which an administrator wrote in the configuration, never from the person signing in.
/// </summary>
public sealed class HttpOidcBackchannel : IOidcBackchannel
{
    private const int MaxBytes = 1024 * 1024;
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, (DateTime At, OidcMetadata Value)> _discovery = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTime At, JsonWebKeySet Value)> _keys = new(StringComparer.Ordinal);

    public HttpOidcBackchannel(HttpClient? http = null) =>
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private static Uri Checked(string address, string what)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new SsoException("provider_error", $"The {what} must be an https address.");
        return uri;
    }

    private async Task<string> GetAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new SsoException("provider_error", $"{uri.Host} answered {(int)response.StatusCode}.");
        return await ReadAsync(response, ct);
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new SsoException("provider_error", "The answer was too large.");
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public async Task<OidcMetadata> DiscoverAsync(string authority, CancellationToken ct = default)
    {
        if (_discovery.TryGetValue(authority, out var known) && DateTime.UtcNow - known.At < Remember)
            return known.Value;

        var uri = Checked(authority.TrimEnd('/') + "/.well-known/openid-configuration", "provider address");
        using var json = JsonDocument.Parse(await GetAsync(uri, ct));
        string Get(string name) => json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new SsoException("provider_error", $"The provider did not give '{name}'.");
        var issuer = Get("issuer");
        // The provider must say it is the one we were configured for; otherwise another server could pose as it.
        if (!string.Equals(issuer.TrimEnd('/'), authority.TrimEnd('/'), StringComparison.Ordinal))
            throw new SsoException("provider_error", "The provider's issuer is not the configured address.");
        var metadata = new OidcMetadata(issuer, Checked(Get("authorization_endpoint"), "authorization address").AbsoluteUri, Checked(Get("token_endpoint"), "token address").AbsoluteUri, Checked(Get("jwks_uri"), "keys address").AbsoluteUri);
        _discovery[authority] = (DateTime.UtcNow, metadata);
        return metadata;
    }

    public async Task<JsonWebKeySet> KeysAsync(string jwksUri, bool refresh, CancellationToken ct = default)
    {
        if (!refresh && _keys.TryGetValue(jwksUri, out var known) && DateTime.UtcNow - known.At < Remember)
            return known.Value;
        var set = new JsonWebKeySet(await GetAsync(Checked(jwksUri, "keys address"), ct));
        _keys[jwksUri] = (DateTime.UtcNow, set);
        return set;
    }

    public async Task<string?> ExchangeAsync(string tokenEndpoint, IReadOnlyDictionary<string, string> form, string? clientId, string? clientSecret, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Checked(tokenEndpoint, "token address"));
        var fields = new Dictionary<string, string>(form);
        if (!string.IsNullOrEmpty(clientSecret))
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(clientId ?? string.Empty)}:{Uri.EscapeDataString(clientSecret)}")));
        else if (clientId is not null)
            fields["client_id"] = clientId; // a public client (no secret) says who it is in the body
        request.Content = new FormUrlEncodedContent(fields);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await ReadAsync(response, ct);
        if (!response.IsSuccessStatusCode)
            throw new SsoException("provider_error", $"The token request was refused ({(int)response.StatusCode}).");
        using var json = JsonDocument.Parse(text);
        return json.RootElement.TryGetProperty("id_token", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
    }
}
