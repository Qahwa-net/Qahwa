using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>
/// The language print formats are written in: your own HTML with <c>{{field}}</c> for a value, <c>{{label:field}}</c> for a field's
/// label, <c>{{#if field}}…{{else}}…{{/if}}</c>, and <c>{{#each table}}…{{/each}}</c> for the rows of a table (with <c>{{@index}}</c>).
/// Values are always HTML-escaped; there is no code, no includes and no expressions.
/// </summary>
public static partial class PrintTemplate
{
    public const int MaxTemplate = 100_000;
    public const int MaxOutput = 2_000_000;
    public const int MaxDepth = 6;
    public static readonly string[] Specials = ["name", "doctype", "today", "user", "status"];

    private abstract record Node;
    private sealed record Text(string Value) : Node;
    private sealed record Var(string Name, bool Label) : Node;
    private sealed record If(string Name, List<Node> Then, List<Node> Else) : Node;
    private sealed record Each(string Table, List<Node> Body) : Node;

    [GeneratedRegex(@"\{\{(.*?)\}\}", RegexOptions.Singleline)]
    private static partial Regex Tag();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Ident();

    // What is never allowed in a template: anything that can run code, load something, or submit something.
    [GeneratedRegex(@"<\s*/?\s*(script|iframe|frame|frameset|object|embed|applet|base|link|meta|form|input|button|textarea|select|svg|math|audio|video|source|template|portal)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenTag();

    [GeneratedRegex(@"[\s""'/]on[a-z]+\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex EventAttribute();

    [GeneratedRegex(@"(javascript|vbscript|data)\s*:(?!image/(png|jpeg|gif|webp);base64,)", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptUrl();

    [GeneratedRegex(@"@import|expression\s*\(|behavior\s*:|-moz-binding|url\s*\(\s*['""]?\s*(?!https:|data:image/)", RegexOptions.IgnoreCase)]
    private static partial Regex ForbiddenCss();

    /// <summary>Why a piece of HTML cannot be used in a print format or letter head, or null when it is fine.</summary>
    public static string? UnsafeReason(string html)
    {
        if (html.Length > MaxTemplate)
            return $"At most {MaxTemplate:N0} characters.";
        if (html.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            return "Control characters are not allowed.";
        if (ForbiddenTag().Match(html) is { Success: true } tag)
            return $"The tag {tag.Value.Trim('<', '>', '/', ' ')} is not allowed: formats are for layout, not code or forms.";
        if (EventAttribute().IsMatch(html))
            return "Attributes like onclick are not allowed.";
        if (ScriptUrl().IsMatch(html))
            return "Links may not start with javascript: or data: (except small embedded images).";
        if (ForbiddenCss().IsMatch(html))
            return "Style rules may not import files or load anything except https and embedded images.";
        return null;
    }

    private static List<Node> Parse(string template, List<string> errors)
    {
        var root = new List<Node>();
        var stack = new Stack<(string Kind, List<Node> Target, object Owner)>();
        var current = root;
        var position = 0;
        foreach (Match match in Tag().Matches(template))
        {
            if (match.Index > position)
                current.Add(new Text(template[position..match.Index]));
            position = match.Index + match.Length;
            var tag = match.Groups[1].Value.Trim();

            if (tag.StartsWith("#if ", StringComparison.Ordinal) && Ident().IsMatch(tag[4..].Trim()))
            {
                var node = new If(tag[4..].Trim(), [], []);
                current.Add(node);
                stack.Push(("if", current, node));
                current = node.Then;
            }
            else if (tag == "else")
            {
                if (stack.Count == 0 || stack.Peek().Owner is not If open || ReferenceEquals(current, open.Else))
                    errors.Add("{{else}} without a matching {{#if}}.");
                else
                    current = open.Else;
            }
            else if (tag == "/if")
            {
                if (stack.Count == 0 || stack.Peek().Kind != "if")
                    errors.Add("{{/if}} without a matching {{#if}}.");
                else
                    current = stack.Pop().Target;
            }
            else if (tag.StartsWith("#each ", StringComparison.Ordinal) && Ident().IsMatch(tag[6..].Trim()))
            {
                var node = new Each(tag[6..].Trim(), []);
                current.Add(node);
                stack.Push(("each", current, node));
                current = node.Body;
            }
            else if (tag == "/each")
            {
                if (stack.Count == 0 || stack.Peek().Kind != "each")
                    errors.Add("{{/each}} without a matching {{#each}}.");
                else
                    current = stack.Pop().Target;
            }
            else if (tag.StartsWith("label:", StringComparison.Ordinal) && Ident().IsMatch(tag[6..].Trim()))
                current.Add(new Var(tag[6..].Trim(), Label: true));
            else if (tag == "@index" || Ident().IsMatch(tag))
                current.Add(new Var(tag, Label: false));
            else
                errors.Add($"'{{{{{(tag.Length > 30 ? tag[..30] : tag)}}}}}' is not something a print format understands.");
        }

        if (position < template.Length)
            current.Add(new Text(template[position..]));
        foreach (var open in stack)
            errors.Add(open.Kind == "if" ? "{{#if}} is never closed with {{/if}}." : "{{#each}} is never closed with {{/each}}.");
        if (Depth(root) > MaxDepth)
            errors.Add($"Blocks are nested more than {MaxDepth} deep.");
        return root;
    }

    private static int Depth(List<Node> nodes) =>
        nodes.Select(n => n switch { If i => 1 + Math.Max(Depth(i.Then), Depth(i.Else)), Each e => 1 + Depth(e.Body), _ => 0 }).DefaultIfEmpty(0).Max();

    /// <summary>Problems with a template for a DocType, keyed <c>html</c>: unsafe HTML, broken blocks, and names that are not fields.</summary>
    public static List<FieldError> Validate(string template, DocTypeMeta meta, IMetaRegistry registry)
    {
        var errors = new List<FieldError>();
        if (string.IsNullOrWhiteSpace(template))
            return [new FieldError("html", "Write the format: HTML with {{field}} where values go.")];
        if (UnsafeReason(template) is { } reason)
            return [new FieldError("html", reason)];

        var problems = new List<string>();
        var tree = Parse(template, problems);
        errors.AddRange(problems.Distinct().Select(p => new FieldError("html", p)));
        if (problems.Count > 0)
            return errors;

        var known = meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type != FieldType.Password).Select(f => f.Name)
            .Concat(StandardFields.Columns.Keys).Concat(Specials).ToHashSet(StringComparer.Ordinal);
        var tables = meta.Fields.Where(f => f.Type == FieldType.Table).ToDictionary(f => f.Name, StringComparer.Ordinal);

        void Check(List<Node> nodes, HashSet<string> scope, bool inRow)
        {
            foreach (var node in nodes)
            {
                switch (node)
                {
                    case Var v when v.Name == "@index":
                        if (!inRow)
                            errors.Add(new FieldError("html", "{{@index}} only works inside {{#each}}."));
                        break;
                    case Var v when !scope.Contains(v.Name):
                        errors.Add(new FieldError("html", $"'{v.Name}' is not a field{(inRow ? " of the table" : " of this DocType")}."));
                        break;
                    case If i:
                        if (!scope.Contains(i.Name))
                            errors.Add(new FieldError("html", $"'{i.Name}' is not a field{(inRow ? " of the table" : " of this DocType")}."));
                        Check(i.Then, scope, inRow);
                        Check(i.Else, scope, inRow);
                        break;
                    case Each e:
                        if (inRow || !tables.TryGetValue(e.Table, out var table) || !registry.TryGet(table.Options ?? string.Empty, out var child))
                            errors.Add(new FieldError("html", inRow ? "Tables cannot be nested." : $"'{e.Table}' is not a table of this DocType."));
                        else
                            Check(e.Body, child.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type != FieldType.Password).Select(f => f.Name).ToHashSet(StringComparer.Ordinal), inRow: true);
                        break;
                }
            }
        }

        Check(tree, known, inRow: false);
        return errors.DistinctBy(e => e.Message).ToList();
    }

    /// <summary>The text of a value as it appears on paper (not yet escaped).</summary>
    public static string Show(DocFieldMeta? field, object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            bool b => b ? "Yes" : "No",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            decimal m when field?.Type is FieldType.Currency => m.ToString("#,##0.00", CultureInfo.InvariantCulture),
            decimal m => m.ToString("#,##0.##########", CultureInfo.InvariantCulture),
            long or int when field?.Type is FieldType.Int => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("#,##0", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        return text.Replace("\0", string.Empty);
    }

    private static string Html(string text, bool multiline) =>
        multiline ? WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>") : WebUtility.HtmlEncode(text);

    /// <summary>Fills a template in. Every value is escaped; unknown names print as nothing. The caller has already checked the template with <see cref="Validate"/>.</summary>
    public static string Render(
        string template, DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, Func<string, DocTypeMeta?> childMeta, IReadOnlyDictionary<string, string> specials)
    {
        var tree = Parse(template, []);
        var output = new StringBuilder();

        string Lookup(string name, DocTypeMeta scopeMeta, IReadOnlyDictionary<string, object?> row, bool label)
        {
            if (scopeMeta == meta && specials.TryGetValue(name, out var special))
                return label ? Html(name, false) : Html(special, false);
            var field = scopeMeta.Fields.FirstOrDefault(f => f.Name == name);
            if (label)
                return Html(field?.Label ?? name, false);
            if (field is { Type: FieldType.Password })
                return string.Empty;
            return Html(Show(field, row.GetValueOrDefault(name)), field?.Type is FieldType.Text or FieldType.LongText);
        }

        bool Truthy(string name, DocTypeMeta scopeMeta, IReadOnlyDictionary<string, object?> row)
        {
            if (scopeMeta == meta && specials.TryGetValue(name, out var special))
                return special.Length > 0;
            return row.GetValueOrDefault(name) switch
            {
                null => false,
                bool b => b,
                string s => s.Length > 0,
                decimal d => d != 0,
                long l => l != 0,
                int i => i != 0,
                double d => d != 0,
                _ => true,
            };
        }

        void Emit(List<Node> nodes, DocTypeMeta scopeMeta, IReadOnlyDictionary<string, object?> row, int index)
        {
            foreach (var node in nodes)
            {
                if (output.Length > MaxOutput)
                    throw new DocumentValidationException([new FieldError("html", "The printed page is too large.")]);
                switch (node)
                {
                    case Text t:
                        output.Append(t.Value);
                        break;
                    case Var { Name: "@index" }:
                        output.Append(index);
                        break;
                    case Var v:
                        output.Append(Lookup(v.Name, scopeMeta, row, v.Label));
                        break;
                    case If i:
                        Emit(Truthy(i.Name, scopeMeta, row) ? i.Then : i.Else, scopeMeta, row, index);
                        break;
                    case Each e:
                    {
                        var child = scopeMeta.Fields.FirstOrDefault(f => f.Name == e.Table) is { Options: { } childName } ? childMeta(childName) : null;
                        if (child is null || row.GetValueOrDefault(e.Table) is not IEnumerable<object?> rows)
                            break;
                        var n = 0;
                        foreach (var item in rows.OfType<IReadOnlyDictionary<string, object?>>().Take(DocumentService.MaxRows))
                            Emit(e.Body, child, item, ++n);
                        break;
                    }
                }
            }
        }

        Emit(tree, meta, doc, 0);
        return output.ToString();
    }
}
