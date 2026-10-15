using Qahwa.Core;

namespace Qahwa.Testing;

/// <summary>Remembers what would have been sent; can be told to fail.</summary>
public sealed class FakeEmailSender : IEmailSender
{
    private readonly List<EmailMessage> _sent = [];
    private int _failures;

    public IReadOnlyList<EmailMessage> Sent { get { lock (_sent) return [.. _sent]; } }

    /// <summary>The next <paramref name="count"/> sends throw.</summary>
    public void Fail(int count) => _failures = count;

    public Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        lock (_sent)
        {
            if (_failures > 0)
            {
                _failures--;
                throw new InvalidOperationException("The mail server said no.");
            }

            _sent.Add(message);
        }

        return Task.CompletedTask;
    }
}
