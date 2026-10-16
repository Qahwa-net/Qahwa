using Qahwa.Core;

namespace Qahwa.Testing;

/// <summary>Keeps the page it was given and answers with a tiny stand-in PDF, so tests can look at what would have been printed.</summary>
public sealed class FakePdfRenderer(bool available = true) : IPdfRenderer
{
    private readonly List<string> _pages = [];

    public bool Available { get; set; } = available;

    public IReadOnlyList<string> Pages { get { lock (_pages) return [.. _pages]; } }

    public Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
    {
        lock (_pages)
            _pages.Add(html);
        return Task.FromResult("%PDF-1.4 fake"u8.ToArray());
    }
}
