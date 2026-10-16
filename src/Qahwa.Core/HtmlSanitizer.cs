using System.Net;
using System.Text;

namespace Qahwa.Core;

/// <summary>
/// Cleans the HTML of Text Editor fields. It does not try to repair or filter what it is given: it reads tags and text, and writes a
/// new document using only a short list of harmless tags. Everything else is dropped (the text inside is kept, except for scripts and
/// styles, which are removed whole), attributes are dropped except a checked <c>href</c> on links, and every piece of text is escaped.
/// Because the output is built from scratch, no input can smuggle a tag, attribute, scheme or entity through.
/// </summary>
public static class HtmlSanitizer
{
    public const int MaxInputLength = 1_000_000;
    public const int MaxDepth = 40;

    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "p", "br", "div", "span", "b", "strong", "i", "em", "u", "s", "sub", "sup", "ul", "ol", "li",
        "h1", "h2", "h3", "h4", "blockquote", "pre", "code", "hr", "a", "table", "thead", "tbody", "tr", "th", "td",
    };

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal) { "br", "hr" };

    /// <summary>Elements dropped together with everything inside them.</summary>
    private static readonly HashSet<string> DropWhole = new(StringComparer.Ordinal)
    {
        "script", "style", "iframe", "object", "noscript", "template", "textarea", "title", "svg", "math", "head", "xmp", "plaintext", "noembed", "noframes",
    };

    private static readonly string[] Schemes = ["http://", "https://", "mailto:", "tel:"];

    /// <exception cref="FormatException">The text is longer than <see cref="MaxInputLength"/>.</exception>
    public static string Clean(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;
        if (html.Length > MaxInputLength)
            throw new FormatException($"Value must be at most {MaxInputLength} characters.");

        var output = new StringBuilder(html.Length);
        var open = new Stack<string>();
        var i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<')
            {
                if (c == '&')
                    AppendAmpersand(output, html, ref i);
                else
                {
                    AppendText(output, c);
                    i++;
                }

                continue;
            }

            if (StartsWith(html, i, "<!--"))
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? html.Length : end + 3;
                continue;
            }

            if (i + 1 < html.Length && html[i + 1] is '!' or '?')
            {
                var end = html.IndexOf('>', i + 2);
                i = end < 0 ? html.Length : end + 1;
                continue;
            }

            if (!TryReadTag(html, i, out var tag))
            {
                output.Append("&lt;");
                i++;
                continue;
            }

            i = tag.End;
            if (!tag.Closing && DropWhole.Contains(tag.Name))
            {
                if (!tag.SelfClosing)
                    i = SkipTo(html, i, tag.Name);
                continue;
            }

            if (!Allowed.Contains(tag.Name))
                continue; // the tag goes, its text stays

            if (tag.Closing)
            {
                if (Void.Contains(tag.Name) || !open.Contains(tag.Name))
                    continue;
                while (open.Count > 0)
                {
                    var top = open.Pop();
                    output.Append("</").Append(top).Append('>');
                    if (top == tag.Name)
                        break;
                }

                continue;
            }

            if (Void.Contains(tag.Name))
            {
                output.Append('<').Append(tag.Name).Append('>');
                continue;
            }

            if (open.Count >= MaxDepth)
                continue;
            output.Append('<').Append(tag.Name);
            if (tag.Name == "a" && SafeHref(tag.Href) is { } href)
                output.Append(" href=\"").Append(EscapeAttribute(href)).Append("\" target=\"_blank\" rel=\"noopener noreferrer nofollow\"");
            output.Append('>');
            open.Push(tag.Name);
        }

        while (open.Count > 0)
            output.Append("</").Append(open.Pop()).Append('>');
        return output.ToString();
    }

    /// <summary>The text of some HTML, with a line break where a paragraph, line or list item ended (for lists, print and email).</summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrEmpty(html))
            return string.Empty;

        var clean = Clean(html);
        var text = new StringBuilder(clean.Length);
        var i = 0;
        while (i < clean.Length)
        {
            if (clean[i] == '<')
            {
                var end = clean.IndexOf('>', i);
                if (end < 0)
                    break;
                var name = clean[(i + 1)..end].TrimStart('/').Split(' ')[0];
                if (name is "br" or "hr" or "p" or "div" or "li" or "tr" or "h1" or "h2" or "h3" or "h4" or "blockquote" or "pre" && (name is "br" or "hr" || clean[i + 1] == '/'))
                    text.Append('\n');
                else if (name is "td" or "th" && clean[i + 1] == '/')
                    text.Append('\t');
                i = end + 1;
            }
            else
            {
                text.Append(clean[i]);
                i++;
            }
        }

        return WebUtility.HtmlDecode(text.ToString()).Trim();
    }

    // ---- reading ----------------------------------------------------------------------------------------------

    private readonly record struct Tag(string Name, bool Closing, bool SelfClosing, string? Href, int End);

    private static bool StartsWith(string text, int at, string value) => string.CompareOrdinal(text, at, value, 0, value.Length) == 0;

    private static bool TryReadTag(string html, int start, out Tag tag)
    {
        tag = default;
        var i = start + 1;
        var closing = false;
        if (i < html.Length && html[i] == '/')
        {
            closing = true;
            i++;
        }

        var nameStart = i;
        if (i >= html.Length || !char.IsAsciiLetter(html[i]))
            return false;
        while (i < html.Length && char.IsAsciiLetterOrDigit(html[i]))
            i++;
        var name = html[nameStart..i].ToLowerInvariant();

        string? href = null;
        var selfClosing = false;
        while (i < html.Length)
        {
            var c = html[i];
            if (c == '>')
            {
                tag = new Tag(name, closing, selfClosing, href, i + 1);
                return true;
            }

            if (char.IsWhiteSpace(c) || c == '/')
            {
                selfClosing = c == '/';
                i++;
                continue;
            }

            selfClosing = false;
            var attrStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
                i++;
            var attr = html[attrStart..i].ToLowerInvariant();
            while (i < html.Length && char.IsWhiteSpace(html[i]))
                i++;
            string? value = null;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                    i++;
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    var quote = html[i++];
                    var close = html.IndexOf(quote, i);
                    if (close < 0)
                        return false; // an unterminated attribute: not a tag
                    value = html[i..close];
                    i = close + 1;
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                        i++;
                    value = html[valueStart..i];
                }
            }

            if (attr == "href" && href is null)
                href = value;
        }

        return false;
    }

    /// <summary>Skips the content of a dropped element, up to just after its closing tag (or to the end of the text).</summary>
    private static int SkipTo(string html, int from, string name)
    {
        var close = "</" + name;
        var at = from;
        while (true)
        {
            at = html.IndexOf(close, at, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return html.Length;
            var end = html.IndexOf('>', at);
            return end < 0 ? html.Length : end + 1;
        }
    }

    // ---- writing ----------------------------------------------------------------------------------------------

    private static void AppendText(StringBuilder output, char c)
    {
        switch (c)
        {
            case '>': output.Append("&gt;"); break;
            case '"': output.Append("&quot;"); break;
            case '\t' or '\n' or '\r': output.Append(c); break;
            default:
                if (!char.IsControl(c))
                    output.Append(c);
                break;
        }
    }

    /// <summary>A real character reference (&amp;amp;, &amp;#8212;) is kept as it is; a lone ampersand becomes one.</summary>
    private static void AppendAmpersand(StringBuilder output, string html, ref int i)
    {
        var end = i + 1;
        if (end < html.Length && html[end] == '#')
        {
            end++;
            var hex = end < html.Length && html[end] is 'x' or 'X';
            if (hex)
                end++;
            var digitsStart = end;
            while (end < html.Length && end - digitsStart < 8 && (hex ? char.IsAsciiHexDigit(html[end]) : char.IsAsciiDigit(html[end])))
                end++;
            if (end > digitsStart && end < html.Length && html[end] == ';')
            {
                output.Append(html, i, end - i + 1);
                i = end + 1;
                return;
            }
        }
        else
        {
            while (end < html.Length && end - i < 10 && char.IsAsciiLetterOrDigit(html[end]))
                end++;
            if (end > i + 1 && end < html.Length && html[end] == ';')
            {
                output.Append(html, i, end - i + 1);
                i = end + 1;
                return;
            }
        }

        output.Append("&amp;");
        i++;
    }

    private static string? SafeHref(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000)
            return null;
        var text = WebUtility.HtmlDecode(value).Trim(); // a character reference can spell a scheme ("&#106;avascript:"): judge the decoded address
        // Browsers ignore tabs, newlines and spaces inside a scheme ("java\tscript:"): judge the address without them.
        var squeezed = new string(text.Where(c => c > ' ').ToArray()).ToLowerInvariant();
        if (squeezed.Any(char.IsControl))
            return null;
        return Schemes.Any(s => squeezed.StartsWith(s, StringComparison.Ordinal)) ? text : null;
    }

    private static string EscapeAttribute(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}
