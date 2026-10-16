using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Core;
using Xunit;

namespace Qahwa.Api.Tests;

public sealed class PdfApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<string> SetUpAsync()
    {
        var made = await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Offer", module = "Buying", autoname = "OF-.####",
                fields = new object[] { new { name = "title", type = "Data" } },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var created = await _factory.ClientFor("s@example.com", "Sales Manager").PostAsJsonAsync("/api/resource/Offer", new { title = "Big <b>deal</b>" });
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("name").GetString()!;
    }

    [Fact]
    public async Task A_pdf_downloads_for_someone_who_may_read_the_document()
    {
        var name = await SetUpAsync();
        var sales = _factory.ClientFor("s@example.com", "Sales Manager");

        Assert.True((await sales.GetFromJsonAsync<JsonElement>("/api/pdf/status")).GetProperty("data").GetProperty("available").GetBoolean());
        var response = await sales.GetAsync($"/api/pdf/Offer/{name}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains($"Offer-{name}.pdf", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.StartsWith("%PDF", await response.Content.ReadAsStringAsync());
        var page = Assert.Single(_factory.Pdf.Pages);
        Assert.Contains("Big &lt;b&gt;deal&lt;/b&gt;", page);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Others_are_refused_and_a_server_without_a_browser_says_501()
    {
        var name = await SetUpAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("o@example.com", "Outsider").GetAsync($"/api/pdf/Offer/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync($"/api/pdf/Offer/{name}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/pdf/status")).StatusCode);
        Assert.Empty(_factory.Pdf.Pages);

        _factory.Pdf.Available = false;
        var none = await _factory.ClientFor("s@example.com", "Sales Manager").GetAsync($"/api/pdf/Offer/{name}");
        Assert.Equal(HttpStatusCode.NotImplemented, none.StatusCode);
        Assert.Equal("pdf_not_available", (await none.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}

/// <summary>The real thing: a browser on this machine prints a page to PDF. Skipped where there is no Chrome, Chromium or Edge.</summary>
public class BrowserPdfRendererTests
{
    private static string[] LeftOver() => Directory.GetDirectories(Path.GetTempPath(), "qahwa-pdf-*");

    [SkippableFact]
    public async Task A_real_browser_makes_a_pdf_and_cleans_up_after_itself()
    {
        var renderer = new BrowserPdfRenderer(new PdfOptions());
        Skip.IfNot(renderer.Available, "No Chrome, Chromium or Edge on this machine.");
        var before = LeftOver().Length;

        var pdf = await renderer.RenderAsync(PdfService.Prepare("<!doctype html><html><head><meta charset=\"utf-8\"></head><body><h1>Quote QT-0001</h1><p>Total 120.00</p><p dir=\"rtl\">مرحبا بالعالم</p></body></html>"));

        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(pdf.Length > 1000);
        Assert.Equal(before, LeftOver().Length);
    }

    [SkippableFact]
    public async Task A_page_that_asks_for_other_addresses_gets_nothing_and_does_not_hang()
    {
        var renderer = new BrowserPdfRenderer(new PdfOptions { TimeoutSeconds = 20 });
        Skip.IfNot(renderer.Available, "No Chrome, Chromium or Edge on this machine.");
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var pdf = await renderer.RenderAsync(PdfService.Prepare("<html><head></head><body><img src=\"http://127.0.0.1:9/x.png\"><img src=\"http://10.255.255.1/x.png\"><p>Still prints</p></body></html>"));

        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task Without_a_browser_the_renderer_says_so()
    {
        var renderer = new BrowserPdfRenderer(new PdfOptions { BrowserPath = Path.Combine(Path.GetTempPath(), "no-such-browser.exe") });

        Assert.False(renderer.Available);
        await Assert.ThrowsAsync<PdfNotAvailableException>(() => renderer.RenderAsync("<p>x</p>"));
    }
}
