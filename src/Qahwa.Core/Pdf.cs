using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>Turns a finished, self-contained HTML page into a PDF file.</summary>
public interface IPdfRenderer
{
    /// <summary>False when nothing is set up to make PDFs on this server.</summary>
    bool Available { get; }

    Task<byte[]> RenderAsync(string html, CancellationToken ct = default);
}

public sealed class PdfNotAvailableException()
    : QahwaException("PDF files cannot be made on this server: no browser is set up for it. Use Print and choose \"Save as PDF\" instead, or ask whoever runs the system to set Qahwa:Pdf:BrowserPath.", 501, "pdf_not_available");

public sealed class PdfBusyException()
    : QahwaException("Too many PDF files are being made right now. Try again in a moment.", 429, "busy");

public sealed record PdfFile(byte[] Content, string FileName);

/// <summary>
/// PDF files of printouts: the same page as the print preview (so the same formats, letter heads and permissions), turned into a PDF by
/// <see cref="IPdfRenderer"/>. The page is made inert first: its scripts are removed, and it may load nothing from anywhere, so a format
/// or a letter head can never make the server fetch an address or read a file (put logos in as data: pictures).
/// </summary>
public sealed partial class PdfService(PrintService print, IPdfRenderer renderer, HitCounter hits)
{
    public const int PerMinute = 10;

    /// <summary>The policy written into the page itself: no scripts, no network, only pictures and fonts that are part of the page.</summary>
    public const string Policy = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; base-uri 'none'; form-action 'none'";

    public bool Available => renderer.Available;

    [GeneratedRegex(@"<script\b[^>]*>.*?</script\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Scripts();

    [GeneratedRegex(@"<div class=""no-print"">.*?</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PrintButton();

    [GeneratedRegex(@"<(?:link|iframe|object|embed|base|meta\s+http-equiv)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Loaders();

    /// <summary>The page without scripts, the Print button or anything that loads other content, and with the policy as its first tag.</summary>
    public static string Prepare(string html)
    {
        html = Scripts().Replace(html, string.Empty);
        html = PrintButton().Replace(html, string.Empty);
        html = Loaders().Replace(html, string.Empty);
        var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        var meta = $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Policy}\">";
        return head < 0 ? meta + html : html.Insert(head + "<head>".Length, meta);
    }

    public async Task<PdfFile> RenderAsync(string doctype, string name, string? format, string? letterHead, IUserContext user, CancellationToken ct = default)
    {
        if (!renderer.Available)
            throw new PdfNotAvailableException();
        var page = await print.RenderAsync(doctype, name, format, letterHead, user, ct); // read access is checked here
        if (!hits.TryHit($"pdf:{user.User}", PerMinute, TimeSpan.FromMinutes(1)))
            throw new RateLimitedException();

        var bytes = await renderer.RenderAsync(Prepare(page.Html), ct);
        var safe = new string($"{doctype}-{name}".Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return new PdfFile(bytes, (safe.Length > 100 ? safe[..100] : safe) + ".pdf");
    }
}

/// <summary>The renderer used when none is set up: PDF files are simply not offered.</summary>
public sealed class NoPdfRenderer : IPdfRenderer
{
    public bool Available => false;

    public Task<byte[]> RenderAsync(string html, CancellationToken ct = default) => throw new PdfNotAvailableException();
}
