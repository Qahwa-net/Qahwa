using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class PrintTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]);
    private static readonly UserContext Lowly = new("lou@example.com", ["Lowly"]); // cannot see the cost

    public PrintTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Bill Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" }, { "name": "rate", "type": "Currency" } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Invoice", "isSubmittable": true, "autoname": "INV-.####", "titleField": "customer", "fields": [
                { "name": "customer", "type": "Data", "label": "Customer" }, { "name": "notes", "type": "Text", "label": "Notes" }, { "name": "total", "type": "Currency", "label": "Total" },
                { "name": "paid", "type": "Check", "label": "Paid" }, { "name": "due_on", "type": "Date", "label": "Due on" }, { "name": "cost", "type": "Currency", "label": "Cost", "permLevel": 1 },
                { "name": "pin", "type": "Password" }, { "name": "lines", "type": "Table", "options": "Bill Line" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "submit": true },
                { "role": "Lowly", "read": true },
                { "role": "Clerk", "read": true, "write": true, "permLevel": 1 } ] }
            """));
    }

    private async Task<string> InvoiceAsync(params (string, object?)[] extra)
    {
        var values = Values([("customer", "Acme"), ("total", 1234.5m), ("due_on", "2026-03-04"), ("paid", true), ("cost", 99)]);
        foreach (var (key, value) in extra)
            values[key] = value;
        values["lines"] = new List<Dictionary<string, object?>> { Values(("item", "Tea"), ("qty", 2), ("rate", 3.5m)), Values(("item", "<b>Sugar</b>"), ("qty", 1000), ("rate", 1)) };
        return (string)(await _engine.Documents.InsertAsync("Invoice", values, Ann))["name"]!;
    }

    private Task<PrintFormatDetail> SaveAsync(string html, string name = "Standard", bool isDefault = false, string? letterHead = null, string doctype = "Invoice") =>
        _engine.Print.SaveFormatAsync(new PrintFormatRequest(name, doctype, html, isDefault, letterHead), TestUsers.SystemManager);

    private static async Task<string> PageAsync(Task<PrintPage> page) => (await page).Html;

    // ---- the template language ----

    [Fact]
    public async Task Values_labels_conditions_and_table_rows_are_filled_in()
    {
        await SaveAsync("""
            <h1>{{label:customer}}: {{customer}}</h1><p>{{name}} / {{doctype}} / {{status}} / {{today}}</p>
            <p>Total {{total}} due {{due_on}} paid {{paid}}</p>
            {{#if paid}}PAID{{else}}OWING{{/if}}
            <table>{{#each lines}}<tr><td>{{@index}}</td><td>{{item}}</td><td>{{qty}}</td><td>{{rate}}</td></tr>{{/each}}</table>
            """);
        var id = await InvoiceAsync();

        var html = (await _engine.Print.RenderAsync("Invoice", id, "Standard", null, Ann)).Html;

        Assert.Contains("<h1>Customer: Acme</h1>", html);
        Assert.Contains($"{id} / Invoice / Draft / 2026-01-01", html);
        Assert.Contains("Total 1,234.50 due 2026-03-04 paid Yes", html);
        Assert.Contains("PAID", html);
        Assert.DoesNotContain("OWING", html);
        Assert.Contains("<td>1</td><td>Tea</td><td>2</td><td>3.50</td>", html);
        Assert.Contains("<td>2</td><td>&lt;b&gt;Sugar&lt;/b&gt;</td><td>1,000</td>", html);
    }

    [Fact]
    public async Task Every_value_is_escaped_so_a_document_cannot_inject_markup_or_script()
    {
        await SaveAsync("<p>{{customer}}</p><p>{{notes}}</p>");
        var id = await InvoiceAsync(("customer", "<script>alert(1)</script>"), ("notes", "line1\nline2 & \"quoted\" <img src=x onerror=alert(1)>"));

        var html = (await _engine.Print.RenderAsync("Invoice", id, "Standard", null, Ann)).Html;

        Assert.Contains("<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>", html);
        Assert.Contains("line1<br>line2 &amp; &quot;quoted&quot; &lt;img src=x onerror=alert(1)&gt;", html);
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.DoesNotContain("<img", html);
    }

    [Fact]
    public async Task A_value_that_looks_like_a_placeholder_is_printed_as_text_and_not_expanded()
    {
        await SaveAsync("<p>{{customer}}</p>");
        var id = await InvoiceAsync(("customer", "{{notes}} {{#each lines}}"));

        var html = (await _engine.Print.RenderAsync("Invoice", id, "Standard", null, Ann)).Html;

        Assert.Contains("<p>{{notes}} {{#each lines}}</p>", html);
    }

    [Fact]
    public async Task Password_fields_never_print_and_fields_above_the_viewers_level_are_not_in_the_document()
    {
        var id = await InvoiceAsync();
        await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync("{{pin}}")); // not offered as a field at all

        await SaveAsync("<p>cost={{cost}}</p>");
        Assert.Contains("cost=99", (await _engine.Print.RenderAsync("Invoice", id, "Standard", null, Ann)).Html);
        Assert.Contains("cost=</p>", (await _engine.Print.RenderAsync("Invoice", id, "Standard", null, Lowly)).Html); // Lowly cannot see it

        var auto = (await _engine.Print.RenderAsync("Invoice", id, null, null, Lowly)).Html;
        Assert.DoesNotContain("Cost", auto);
        Assert.DoesNotContain("<td>99", auto);
    }

    // ---- validation of templates ----

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("<SCRIPT src=x>")]
    [InlineData("<iframe src=x></iframe>")]
    [InlineData("<object data=x>")]
    [InlineData("<form action=x><input></form>")]
    [InlineData("<svg onload=alert(1)>")]
    [InlineData("<meta http-equiv=refresh content=0>")]
    [InlineData("<base href=http://evil/>")]
    [InlineData("<link rel=stylesheet href=x>")]
    [InlineData("<p onclick=\"x()\">a</p>")]
    [InlineData("<p style=x ONMOUSEOVER = x>a</p>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>")]
    [InlineData("<a href=\" JaVaScRiPt:alert(1)\">x</a>")]
    [InlineData("<img src=\"data:text/html;base64,AAAA\">")]
    [InlineData("<style>@import url(http://evil/x.css);</style>")]
    [InlineData("<style>p{background:url(http://evil/x.png)}</style>")]
    [InlineData("<style>p{width:expression(alert(1))}</style>")]
    public async Task Dangerous_html_is_refused(string html)
    {
        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(html))).Errors;
        Assert.Equal("html", errors[0].Field);
    }

    [Theory]
    [InlineData("<p>{{customer}}</p>")]
    [InlineData("<img src=\"data:image/png;base64,iVBOR\"> <style>h1{color:#333;background:url(https://example.com/a.png)}</style>")]
    [InlineData("<a href=\"https://example.com\">link</a> <div style=\"font-weight:bold\">x</div> <table><tr><td>y</td></tr></table>")]
    public async Task Ordinary_layout_html_is_accepted(string html) => Assert.Equal("Standard", (await SaveAsync(html)).Name);

    [Fact]
    public async Task Mistakes_in_a_template_are_reported_in_words()
    {
        async Task<string> Message(string html) => (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(html))).Errors.First().Message;

        Assert.Contains("'nope' is not a field of this DocType", await Message("{{nope}}"));
        Assert.Contains("never closed", await Message("{{#if paid}}x"));
        Assert.Contains("without a matching", await Message("{{/if}}"));
        Assert.Contains("without a matching", await Message("{{else}}"));
        Assert.Contains("is not a table", await Message("{{#each customer}}x{{/each}}"));
        Assert.Contains("is not a field of the table", await Message("{{#each lines}}{{customer}}{{/each}}"));
        Assert.Contains("cannot be nested", await Message("{{#each lines}}{{#each lines}}x{{/each}}{{/each}}"));
        Assert.Contains("only works inside", await Message("{{@index}}"));
        Assert.Contains("not something a print format understands", await Message("{{ 1 + 1 }}"));
        Assert.Contains("not something a print format understands", await Message("{{customer.owner}}"));
        Assert.Contains("nested more than", await Message(string.Concat(Enumerable.Repeat("{{#if paid}}", 7)) + string.Concat(Enumerable.Repeat("{{/if}}", 7))));
        Assert.Contains("Write the format", await Message("   "));
        Assert.Contains("At most", await Message(new string('x', PrintTemplate.MaxTemplate + 1)));
    }

    // ---- default layout and the page ----

    [Fact]
    public async Task Without_a_format_a_clean_layout_of_the_fields_and_tables_is_made()
    {
        var id = await InvoiceAsync();

        var page = await _engine.Print.RenderAsync("Invoice", id, null, null, Ann);

        Assert.Contains("<h1>Acme</h1>", page.Html);
        Assert.Contains("<th>Total</th><td>1,234.50</td>", page.Html);
        Assert.Contains("<th>Paid</th><td>Yes</td>", page.Html);
        Assert.Contains("&lt;b&gt;Sugar&lt;/b&gt;", page.Html);
        Assert.DoesNotContain("Pin", page.Html);
        Assert.StartsWith("<!doctype html>", page.Html);
        Assert.Contains($"nonce=\"{page.Nonce}\"", page.Html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page.Html, "<script")); // only the page own print button
    }

    [Fact]
    public async Task The_default_format_is_used_when_none_is_asked_for_and_only_one_is_the_default()
    {
        await SaveAsync("A:{{customer}}", "A", isDefault: true);
        await SaveAsync("B:{{customer}}", "B", isDefault: true);
        var id = await InvoiceAsync();

        Assert.Contains("B:Acme", await PageAsync(_engine.Print.RenderAsync("Invoice", id, null, null, Ann)));
        Assert.Contains("A:Acme", await PageAsync(_engine.Print.RenderAsync("Invoice", id, "A", null, Ann)));
        Assert.Equal(["B"], (await _engine.Print.ListFormatsAsync(TestUsers.SystemManager)).Where(f => f.IsDefault).Select(f => f.Name));
    }

    [Fact]
    public async Task A_letter_head_wraps_the_page_and_none_removes_it()
    {
        await _engine.Print.SaveLetterHeadAsync(new LetterHeadRequest("Company", "<h2>ACME Ltd</h2>", "<small>Thanks</small>", IsDefault: true), TestUsers.SystemManager);
        await _engine.Print.SaveLetterHeadAsync(new LetterHeadRequest("Plain", "<i>P</i>", ""), TestUsers.SystemManager);
        await SaveAsync("{{customer}}", "Standard", letterHead: "Plain");
        var id = await InvoiceAsync();

        var withFormatHead = await PageAsync(_engine.Print.RenderAsync("Invoice", id, "Standard", null, Ann));
        Assert.Contains("<i>P</i>", withFormatHead);
        Assert.DoesNotContain("ACME", withFormatHead);

        var chosen = await PageAsync(_engine.Print.RenderAsync("Invoice", id, "Standard", "Company", Ann));
        Assert.Contains("<h2>ACME Ltd</h2>", chosen);
        Assert.Contains("<small>Thanks</small>", chosen);

        Assert.Contains("ACME", await PageAsync(_engine.Print.RenderAsync("Invoice", id, null, null, Ann))); // the default one
        Assert.DoesNotContain("lh-header\">", await PageAsync(_engine.Print.RenderAsync("Invoice", id, "Standard", "none", Ann)));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.RenderAsync("Invoice", id, "Standard", "Missing", Ann));
    }

    [Fact]
    public async Task Letter_heads_are_checked_for_dangerous_html_too()
    {
        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Print.SaveLetterHeadAsync(new LetterHeadRequest("Bad", "<script>x</script>", "<img src=x onerror=y>"), TestUsers.SystemManager))).Errors.Select(e => e.Field);
        Assert.Contains("header", errors);
        Assert.Contains("footer", errors);
    }

    // ---- who may print and manage ----

    [Fact]
    public async Task Printing_needs_access_to_the_document_and_hides_other_peoples_formats()
    {
        await SaveAsync("{{customer}}");
        var id = await InvoiceAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.RenderAsync("Invoice", id, "Standard", null, Eve));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.RenderAsync("Invoice", "NOPE", null, null, Ann));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.RenderAsync("Invoice", id, "Missing", null, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.ChoicesAsync("Invoice", Eve));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Print.ChoicesAsync("Invoice", UserContext.Anonymous));
        Assert.Equal(["Standard"], (await _engine.Print.ChoicesAsync("Invoice", Ann)).Formats.Select(f => f.Name));
    }

    [Fact]
    public async Task A_format_made_for_another_doctype_cannot_be_used_and_only_managers_manage()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Memo", "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "create": true } ] }"""));
        await SaveAsync("{{customer}}");
        var memo = (string)(await _engine.Documents.InsertAsync("Memo", Values(("title", "x")), Ann))["name"]!;

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.RenderAsync("Memo", memo, "Standard", null, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.SaveFormatAsync(new PrintFormatRequest("X", "Invoice", "x"), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.ListFormatsAsync(Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.GetFormatAsync("Standard", Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.DeleteFormatAsync("Standard", Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.SaveLetterHeadAsync(new LetterHeadRequest("X"), Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Print.ListFormatsAsync(UserContext.Anonymous));
    }

    [Fact]
    public async Task Formats_have_checked_names_doctypes_and_limits()
    {
        async Task<string> FieldOf(PrintFormatRequest request) =>
            (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Print.SaveFormatAsync(request, TestUsers.SystemManager))).Errors.First().Field;

        Assert.Equal("name", await FieldOf(new("", "Invoice", "x")));
        Assert.Equal("name", await FieldOf(new("../../etc/passwd", "Invoice", "x")));
        Assert.Equal("name", await FieldOf(new("<b>", "Invoice", "x")));
        Assert.Equal("docType", await FieldOf(new("A", "User", "x"))); // framework DocTypes are not printed with custom formats
        Assert.Equal("letterHead", await FieldOf(new("A", "Invoice", "x", false, "Missing")));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.SaveFormatAsync(new PrintFormatRequest("A", "Nope", "x"), TestUsers.SystemManager));

        for (var i = 0; i < PrintService.MaxFormatsPerDocType; i++)
            await SaveAsync("{{customer}}", $"F{i}");
        Assert.Equal("name", await FieldOf(new("One more", "Invoice", "x")));
        await SaveAsync("{{customer}} changed", "F0"); // changing an existing one is fine
    }

    [Fact]
    public async Task Formats_can_be_read_changed_and_deleted_by_managers()
    {
        await SaveAsync("{{customer}}", "Standard");
        await SaveAsync("{{customer}} v2", "Standard");
        Assert.Equal("{{customer}} v2", (await _engine.Print.GetFormatAsync("Standard", TestUsers.SystemManager)).Html);

        await _engine.Print.DeleteFormatAsync("Standard", TestUsers.SystemManager);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.GetFormatAsync("Standard", TestUsers.SystemManager));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Print.DeleteFormatAsync("Standard", TestUsers.SystemManager));
    }

    [Fact]
    public async Task A_format_can_be_previewed_on_a_real_document_before_it_is_saved()
    {
        var id = await InvoiceAsync();

        var page = await _engine.Print.PreviewAsync("Invoice", id, "<b>{{customer}}</b>", null, TestUsers.Admin);

        Assert.Contains("<b>Acme</b>", page.Html);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Print.PreviewAsync("Invoice", id, "<script>1</script>", null, TestUsers.Admin));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Print.PreviewAsync("Invoice", id, "x{{customer}}", null, Ann));
    }
}
