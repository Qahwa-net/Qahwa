using Qahwa.Core;

namespace Qahwa.Testing;

/// <summary>Remembers what would have been posted; answers with a chosen status, or fails.</summary>
public sealed class FakeWebhookSender : IWebhookSender
{
    private readonly List<(string Url, IReadOnlyDictionary<string, string> Headers, string Body)> _sent = [];

    public IReadOnlyList<(string Url, IReadOnlyDictionary<string, string> Headers, string Body)> Sent { get { lock (_sent) return [.. _sent]; } }

    /// <summary>The status the receiver answers with (default 200).</summary>
    public int Status { get; set; } = 200;

    /// <summary>When set, sending throws this.</summary>
    public Exception? Throw { get; set; }

    public Task<WebhookResult> SendAsync(string url, IReadOnlyDictionary<string, string> headers, string body, CancellationToken ct = default)
    {
        if (Throw is { } ex)
            throw ex;
        lock (_sent)
            _sent.Add((url, headers, body));
        return Task.FromResult(new WebhookResult(Status));
    }
}
