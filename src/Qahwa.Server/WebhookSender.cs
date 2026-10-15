using System.Net;
using System.Net.Sockets;
using System.Text;
using Qahwa.Core;

/// <summary>
/// Posts webhook deliveries. It connects only to public addresses: the destination name is resolved here, every address is
/// checked with <see cref="NetworkGuard"/> at connect time (so a name that later points at an internal address, or changes
/// between the check and the connection, is still refused), redirects are never followed and the whole call is time-limited.
/// </summary>
public sealed class WebhookSender : IWebhookSender, IDisposable
{
    private readonly HttpClient _client;

    public WebhookSender(WebhookOptions options)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
                var allowed = options.AllowInsecure ? addresses : addresses.Where(NetworkGuard.IsPublic).ToArray();
                if (allowed.Length == 0)
                    throw new HttpRequestException("The address is not a public one, so nothing was sent.");
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 64 * 1024 };
    }

    public async Task<WebhookResult> SendAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (key, value) in headers)
            request.Headers.TryAddWithoutValidation(key, value);
        request.Headers.UserAgent.ParseAdd("Qahwa-Webhook/1");
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new WebhookResult((int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new WebhookResult(0, ex is TaskCanceledException ? "The receiver did not answer in time." : ex.Message);
        }
    }

    public void Dispose() => _client.Dispose();
}
