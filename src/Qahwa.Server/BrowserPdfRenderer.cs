using System.Diagnostics;
using System.Text;
using Qahwa.Core;

/// <summary>Settings under <c>Qahwa:Pdf</c>.</summary>
public sealed class PdfOptions
{
    /// <summary>The Chrome, Chromium or Edge program that makes PDFs. Empty: look in the usual places.</summary>
    public string? BrowserPath { get; set; }

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxMegabytes { get; set; } = 25;

    /// <summary>How many PDF files are made at the same time; others wait a few seconds, then are told to try again.</summary>
    public int Concurrency { get; set; } = 2;

    /// <summary>Needed by Chrome when it runs as root inside some containers. Off by default: the sandbox is a protection.</summary>
    public bool NoSandbox { get; set; }
}

/// <summary>
/// Makes PDFs by asking a headless Chrome, Chromium or Edge to print the page. It is started without a shell (arguments are passed as a list),
/// in a folder of its own that is deleted afterwards, with no network (a dead proxy, so even an address given as numbers goes nowhere), a
/// time limit after which it is killed, and a limit on the size of the result.
/// </summary>
public sealed class BrowserPdfRenderer : IPdfRenderer
{
    private static readonly string[] Candidates =
    [
        @"C:\Program Files\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
        @"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
        "/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/snap/bin/chromium", "/usr/bin/microsoft-edge",
        "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    ];

    private readonly PdfOptions _options;
    private readonly SemaphoreSlim _slots;
    private readonly string? _path;

    public BrowserPdfRenderer(PdfOptions options)
    {
        _options = options;
        _path = !string.IsNullOrWhiteSpace(options.BrowserPath) ? (File.Exists(options.BrowserPath) ? options.BrowserPath : null) : Candidates.FirstOrDefault(File.Exists);
        _slots = new SemaphoreSlim(Math.Clamp(options.Concurrency, 1, 8));
    }

    public bool Available => _path is not null;

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // it had just exited
        }
    }

    /// <summary>A PDF that is finished: it ends with the end-of-file marker and did not change since the last look.</summary>
    private static async Task<bool> IsCompletePdfAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
                return false;
            var first = new FileInfo(path).Length;
            if (first < 8)
                return false;
            await Task.Delay(100, ct);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length != first)
                return false;
            var tail = new byte[Math.Min(stream.Length, 1024)];
            stream.Seek(-tail.Length, SeekOrigin.End);
            await stream.ReadExactlyAsync(tail, ct);
            return Encoding.ASCII.GetString(tail).Contains("%%EOF", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false; // still being written
        }
    }

    public async Task<byte[]> RenderAsync(string html, CancellationToken ct = default)
    {
        if (_path is null)
            throw new PdfNotAvailableException();
        if (!await _slots.WaitAsync(TimeSpan.FromSeconds(10), ct))
            throw new PdfBusyException();

        var folder = Path.Combine(Path.GetTempPath(), "qahwa-pdf-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var page = Path.Combine(folder, "page.html");
            var output = Path.Combine(folder, "out.pdf");
            await File.WriteAllTextAsync(page, html, ct);

            var start = new ProcessStartInfo(_path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var argument in new[]
            {
                "--headless=new", "--disable-gpu", "--disable-dev-shm-usage", "--disable-background-networking", "--disable-sync",
                "--no-first-run", "--no-default-browser-check", "--mute-audio", "--hide-scrollbars", "--no-pdf-header-footer",
                "--proxy-server=127.0.0.1:9", "--host-resolver-rules=MAP * ~NOTFOUND", "--virtual-time-budget=4000",
                $"--user-data-dir={Path.Combine(folder, "profile")}", $"--print-to-pdf={output}",
            })
                start.ArgumentList.Add(argument);
            if (_options.NoSandbox)
                start.ArgumentList.Add("--no-sandbox");
            start.ArgumentList.Add(new Uri(page).AbsoluteUri);

            using var process = Process.Start(start) ?? throw new InvalidOperationException("The browser could not be started.");
            _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 300)));
            try
            {
                // Done when the browser exits or, because some versions linger after printing, as soon as a complete PDF is on disk.
                while (!process.HasExited && !await IsCompletePdfAsync(output, limit.Token))
                    await Task.Delay(50, limit.Token);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                ct.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Making the PDF took too long.");
            }

            if (!process.HasExited)
                Kill(process);

            if (!File.Exists(output))
                throw new InvalidOperationException("The browser did not make a PDF.");
            var info = new FileInfo(output);
            if (info.Length > Math.Max(_options.MaxMegabytes, 1) * 1024L * 1024L)
                throw new InvalidOperationException("The PDF is too large.");
            var bytes = await File.ReadAllBytesAsync(output, ct);
            return bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? bytes : throw new InvalidOperationException("The browser did not make a PDF.");
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A file still in use is cleaned up with the temporary folder later; the answer is already made.
            }

            _slots.Release();
        }
    }
}
