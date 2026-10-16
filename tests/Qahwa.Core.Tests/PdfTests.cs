using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class PdfTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]);

    public PdfTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quote", "autoname": "QT-.####", "fields": [ { "name": "customer", "type": "Data", "label": "Customer" }, { "name": "total", "type": "Currency" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task<string> QuoteAsync(string customer = "Acme") => (string)(await _engine.Documents.InsertAsync("Quote", Values(("customer", customer), ("total", 120m)), Ann))["name"]!;

    [Fact]
    public void The_page_is_made_inert_before_it_is_printed()
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><link rel=\"stylesheet\" href=\"http://evil.example/x.css\"><title>t</title></head><body>" +
                   "<div class=\"no-print\"><button id=\"print-btn\">Print</button></div><h1>Quote</h1><SCRIPT >alert(1)</SCRIPT ><script nonce=\"x\">fetch('http://169.254.169.254/')</script>" +
                   "<iframe src=\"file:///etc/passwd\"></iframe><object data=\"x\"></object><embed src=\"y\"><base href=\"http://evil.example/\"><meta http-equiv=\"refresh\" content=\"0;url=http://evil.example/\">" +
                   "<p>Hello</p></body></html>";

        var ready = PdfService.Prepare(html);

        Assert.DoesNotContain("<script", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(1)", ready);
        Assert.DoesNotContain("169.254", ready);
        Assert.DoesNotContain("<iframe", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<object", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<embed", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<base", ready, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("refresh", ready);
        Assert.DoesNotContain("print-btn", ready);
        Assert.Contains("<h1>Quote</h1>", ready);
        Assert.Contains("<p>Hello</p>", ready);
        Assert.StartsWith("<!doctype html><html><head><meta http-equiv=\"Content-Security-Policy\"", ready); // the policy is the first thing the browser reads
        Assert.Contains("default-src 'none'", ready);
        Assert.Contains("img-src data:", ready);
    }

    [Fact]
    public void A_page_without_a_head_still_gets_the_policy()
    {
        Assert.StartsWith("<meta http-equiv=\"Content-Security-Policy\"", PdfService.Prepare("<p>x</p>"));
    }

    [Fact]
    public async Task A_pdf_is_made_from_the_same_page_as_the_printout_with_a_safe_file_name()
    {
        var name = await QuoteAsync("Acme & <Sons>");

        var file = await _engine.Pdf.RenderAsync("Quote", name, null, null, Ann);

        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(file.Content));
        Assert.Equal("Quote-" + name + ".pdf", file.FileName);
        var page = Assert.Single(_engine.PdfRenderer.Pages);
        Assert.Contains("Acme &amp; &lt;Sons&gt;", page); // values stay escaped
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Whoever_may_not_read_the_document_gets_no_pdf_and_nothing_is_rendered()
    {
        var name = await QuoteAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Pdf.RenderAsync("Quote", name, null, null, Eve));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Pdf.RenderAsync("Quote", "NOPE-1", null, null, Ann));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Pdf.RenderAsync("Quote", name, "No Such Format", null, Ann));
        Assert.Empty(_engine.PdfRenderer.Pages);
    }

    [Fact]
    public async Task Without_a_browser_it_says_so_plainly()
    {
        var name = await QuoteAsync();
        _engine.PdfRenderer.Available = false;

        Assert.False(_engine.Pdf.Available);
        var error = await Assert.ThrowsAsync<PdfNotAvailableException>(() => _engine.Pdf.RenderAsync("Quote", name, null, null, Ann));
        Assert.Equal(501, error.StatusCode);
        Assert.Contains("Save as PDF", error.Message);
    }

    [Fact]
    public async Task One_person_cannot_make_more_than_ten_pdfs_a_minute()
    {
        var name = await QuoteAsync();

        for (var i = 0; i < PdfService.PerMinute; i++)
            await _engine.Pdf.RenderAsync("Quote", name, null, null, Ann);

        await Assert.ThrowsAsync<RateLimitedException>(() => _engine.Pdf.RenderAsync("Quote", name, null, null, Ann));
        _engine.Clock.Advance(TimeSpan.FromMinutes(2));
        await _engine.Pdf.RenderAsync("Quote", name, null, null, Ann);
    }
}
