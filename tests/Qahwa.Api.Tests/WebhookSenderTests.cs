using System.Net;
using System.Net.Sockets;
using System.Text;
using Qahwa.Core;

namespace Qahwa.Api.Tests;

/// <summary>The real sender, against addresses it must never reach (nothing leaves the machine in these tests).</summary>
public class WebhookSenderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    [Theory]
    [InlineData("https://localhost:9/x")]
    [InlineData("https://127.0.0.1:9/x")]
    [InlineData("https://[::1]:9/x")]
    [InlineData("https://10.0.0.1:9/x")]
    [InlineData("https://192.168.1.1:9/x")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://0.0.0.0:9/x")]
    public async Task It_refuses_to_connect_to_internal_addresses(string url)
    {
        using var sender = new WebhookSender(new WebhookOptions());

        var result = await sender.SendAsync(url, NoHeaders, "{}");

        Assert.False(result.Ok);
        Assert.Equal(0, result.StatusCode);
        Assert.Contains("public", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task With_development_mode_it_can_reach_a_local_receiver_and_never_follows_redirects()
    {
        var port = FreePort();
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        var served = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            var buffer = new byte[4096];
            var read = await stream.ReadAsync(buffer);
            var request = Encoding.ASCII.GetString(buffer, 0, read);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:1/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
            return request;
        });
        using var sender = new WebhookSender(new WebhookOptions { AllowInsecure = true });

        var result = await sender.SendAsync($"http://127.0.0.1:{port}/hook", new Dictionary<string, string> { ["X-Qahwa-Event"] = "New" }, "{\"a\":1}");

        Assert.Equal(302, result.StatusCode); // reported as it came, not followed
        Assert.False(result.Ok);
        var request = await served;
        Assert.StartsWith("POST /hook ", request);
        Assert.Contains("X-Qahwa-Event: New", request);
        Assert.Contains("application/json", request);
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
