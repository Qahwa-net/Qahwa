using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class HtmlSanitizerTests
{
    [Theory]
    [InlineData("<p>Hello <b>world</b></p>", "<p>Hello <b>world</b></p>")]
    [InlineData("a &amp; b &#8212; c &copy; d", "a &amp; b &#8212; c &copy; d")]
    [InlineData("1 < 2 and 3 > 2 & more", "1 &lt; 2 and 3 &gt; 2 &amp; more")]
    [InlineData("<ul><li>one<li>two</ul>", "<ul><li>one<li>two</li></li></ul>")]
    [InlineData("<P CLASS=x STYLE='color:red' onclick=alert(1)>Hi</P>", "<p>Hi</p>")]
    [InlineData("line<br/>break<hr>", "line<br>break<hr>")]
    [InlineData("<b>never closed", "<b>never closed</b>")]
    [InlineData("stray </i> close", "stray  close")]
    public void Safe_markup_is_kept_and_the_rest_is_made_safe(string input, string expected) =>
        Assert.Equal(expected, HtmlSanitizer.Clean(input));

    [Theory]
    [InlineData("<script>alert(1)</script>after", "after")]
    [InlineData("<SCRIPT SRC=//evil/x.js></SCRIPT>ok", "ok")]
    [InlineData("<script>alert(1)", "")]
    [InlineData("<style>body{display:none}</style>text", "text")]
    [InlineData("<iframe src=//evil></iframe>x", "x")]
    [InlineData("<img src=x onerror=alert(1)>", "")]
    [InlineData("<svg onload=alert(1)><circle/></svg>t", "t")]
    [InlineData("<math><mi xlink:href=javascript:alert(1)>x</mi></math>y", "y")]
    [InlineData("<object data=x></object><embed src=x>z", "z")]
    [InlineData("<form action=//evil><input name=x></form>q", "q")]
    [InlineData("<!-- <script>alert(1)</script> -->v", "v")]
    [InlineData("<![CDATA[<script>]]>w", "]]&gt;w")]
    [InlineData("<?xml version='1.0'?>m", "m")]
    [InlineData("<p onmouseover=alert(1)>x</p>", "<p>x</p>")]
    [InlineData("<b/onclick=alert(1)>x</b>", "<b>x</b>")]
    public void Scripts_styles_and_event_handlers_never_survive(string input, string expected)
    {
        var clean = HtmlSanitizer.Clean(input);

        Assert.Equal(expected, clean);
        Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", clean, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("<a href=\"https://example.com/a?x=1&amp;y=2\">go</a>", "<a href=\"https://example.com/a?x=1&amp;y=2\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">go</a>")]
    [InlineData("<a href='mailto:a@b.c'>m</a>", "<a href=\"mailto:a@b.c\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">m</a>")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"JaVaScRiPt:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"java\tscript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"&#106;avascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"&#x6A;avascript&colon;alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\" javascript:alert(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"//evil.example/x\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"/relative\">x</a>", "<a>x</a>")]
    [InlineData("<a href=\"https://e.com/\" onclick=\"alert(1)\">x</a>", "<a href=\"https://e.com/\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">x</a>")]
    [InlineData("<a href=\"https://e.com/&quot; onmouseover=&quot;alert(1)\">x</a>", "<a href=\"https://e.com/&quot; onmouseover=&quot;alert(1)\" target=\"_blank\" rel=\"noopener noreferrer nofollow\">x</a>")]
    public void Links_keep_only_checked_web_addresses(string input, string expected) =>
        Assert.Equal(expected, HtmlSanitizer.Clean(input));

    [Fact]
    public void A_quote_in_an_address_cannot_start_a_new_attribute()
    {
        var clean = HtmlSanitizer.Clean("<a href=\"https://e.com/&quot; onmouseover=&quot;alert(1)\">x</a>");

        Assert.DoesNotContain("\" onmouseover", clean);
    }

    [Fact]
    public void Odd_input_does_not_crash_or_hang()
    {
        foreach (var input in new[] { "<", "<<<<", "<a", "<a href=", "<a href=\"x", "</", "</a", "<p <p <p>", "&", "&#", "&#x;", "&amp", "<!--", "<!", "<script", "<a/>", "\0<b>\0", "<b\n>x</b>" })
            Assert.NotNull(HtmlSanitizer.Clean(input));

        var deep = string.Concat(Enumerable.Repeat("<div>", 5000)) + "x";
        var clean = HtmlSanitizer.Clean(deep);
        Assert.Equal(HtmlSanitizer.MaxDepth, clean.Split("<div>").Length - 1);
        Assert.Throws<FormatException>(() => HtmlSanitizer.Clean(new string('a', HtmlSanitizer.MaxInputLength + 1)));
    }

    [Fact]
    public void Cleaning_twice_changes_nothing()
    {
        foreach (var input in new[] { "<p>a &amp; b <b>c</b></p>", "x < y", "<a href=\"https://e.com/?a=1&b=2\">l</a>", "<ul><li>1</ul>" })
        {
            var once = HtmlSanitizer.Clean(input);
            Assert.Equal(once, HtmlSanitizer.Clean(once));
        }
    }

    [Fact]
    public void Plain_text_has_a_break_where_a_paragraph_or_line_ended()
    {
        Assert.Equal("one\ntwo\nthree & four", HtmlSanitizer.ToPlainText("<p>one<br>two</p><p>three &amp; four</p>"));
        Assert.Equal("", HtmlSanitizer.ToPlainText(null));
        Assert.Equal("a < b", HtmlSanitizer.ToPlainText("a &lt; b"));
        Assert.Equal("evil", HtmlSanitizer.ToPlainText("<script>x</script>evil"));
    }

    [Fact]
    public async Task A_text_editor_field_is_cleaned_on_save_and_small_text_is_limited()
    {
        var engine = new TestEngine();
        engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Article", "autoname": "field:title",
              "fields": [ { "name": "title", "type": "Data", "required": true }, { "name": "body", "type": "TextEditor" }, { "name": "blurb", "type": "SmallText" },
                          { "name": "city", "type": "Autocomplete", "options": "Riyadh\nJeddah" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        var ann = new UserContext("ann@example.com", ["Clerk"]);

        var saved = await engine.Documents.InsertAsync("Article", Values(("title", "t"), ("body", "<p onclick=x>Hi</p><script>bad()</script>"), ("blurb", "short"), ("city", "Dammam")), ann);

        Assert.Equal("<p>Hi</p>", saved["body"]);
        Assert.Equal("Dammam", saved["city"]); // any value is allowed
        await Assert.ThrowsAsync<DocumentValidationException>(() => engine.Documents.InsertAsync("Article", Values(("title", "u"), ("blurb", new string('x', FieldValueConverter.MaxSmallTextCharacters + 1))), ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => engine.Documents.InsertAsync("Article", Values(("title", "v"), ("city", new string('c', 141))), ann));
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "Bad", "fields": [ { "name": "c", "type": "Autocomplete", "options": "%LONG%" } ] }""".Replace("%LONG%", new string('z', 141))));
    }
}
