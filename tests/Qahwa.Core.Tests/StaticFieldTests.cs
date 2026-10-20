using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class StaticFieldTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    private DocTypeMeta Define() => DocTypeLoader.Parse("""
        { "name": "Intake", "fields": [
            { "name": "intro", "type": "Heading", "label": "Contact details" },
            { "name": "title", "type": "Data" },
            { "name": "help", "type": "Html", "options": "<p>Read <b>this</b> <a href=\"javascript:alert(1)\" onclick=\"x()\">link</a><script>alert(1)</script><img src=x onerror=alert(1)></p>" } ],
          "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
        """);

    [Fact]
    public void Html_fields_are_cleaned_when_the_DocType_is_defined()
    {
        var html = Define().Fields.Single(f => f.Name == "help").Options!;
        Assert.Contains("<b>this</b>", html);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Headings_and_html_hold_no_value_and_have_no_column()
    {
        _engine.Registry.Add(Define());
        Assert.False(FieldTypes.HasColumn(FieldType.Heading));
        Assert.False(FieldTypes.HasColumn(FieldType.Html));
        var doc = await _engine.Documents.InsertAsync("Intake", Values(("title", "Acme")), Ann);
        Assert.False(doc.ContainsKey("intro"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Intake", Values(("intro", "x")), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Intake", Values(("help", "<b>x</b>")), Ann));
    }
}
