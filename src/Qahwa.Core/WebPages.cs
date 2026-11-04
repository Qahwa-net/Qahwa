using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>A published page as visitors see it: the text is cleaned HTML (see <see cref="HtmlSanitizer"/>).</summary>
public sealed record PublicPage(string Route, string Title, string Html, string? Summary, DateOnly? PublishedOn);

public sealed record PublicPageLink(string Route, string Title, string? Summary, DateOnly? PublishedOn);

/// <summary>
/// Web pages (ADR-160): content pages anyone can read without signing in, at <c>/pages/&lt;address&gt;</c>, with an index at <c>/pages</c>.
/// A System Manager writes them in the Desk; only published pages are ever shown, the HTML is cleaned when it is read (what is stored is
/// never trusted to still be clean), and nothing but the page itself is given to visitors.
/// </summary>
public sealed partial class WebPageService(IMetaRegistry registry, IDocumentRepository repository)
{
    public const string DocTypeName = "Web Page";
    public const int MaxIndex = 200;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,60}$")]
    private static partial Regex RoutePattern();

    public static bool IsRoute(string? route) => route is not null && RoutePattern().IsMatch(route);

    /// <summary>The rules a page must meet: a plain address, and content within the size the cleaner accepts.</summary>
    public static void Validate(IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        if (values.GetValueOrDefault("route") is string route && !IsRoute(route))
            errors.Add(new FieldError("route", "Use lower-case letters, digits and dashes (up to 61), starting with a letter or digit."));
        if (values.GetValueOrDefault("content") is string content && content.Length > HtmlSanitizer.MaxInputLength)
            errors.Add(new FieldError("content", $"The content is too long (at most {HtmlSanitizer.MaxInputLength:N0} characters)."));
    }

    private static bool Published(Dictionary<string, object?> row) => row.GetValueOrDefault("published") is true;

    private static DateOnly? DateOf(Dictionary<string, object?> row) =>
        row.GetValueOrDefault("published_on") switch { DateOnly d => d, DateTime t => DateOnly.FromDateTime(t), _ => null };

    /// <summary>One published page, or "not found" for anything else (a draft looks like a page that does not exist).</summary>
    public async Task<PublicPage> GetAsync(string route, CancellationToken ct = default)
    {
        var row = IsRoute(route) ? await repository.GetAsync(registry.Get(DocTypeName), route, ct) : null;
        if (row is null || !Published(row))
            throw new NotFoundException("This page is not available.");
        return new PublicPage(route, row["title"] as string ?? route, HtmlSanitizer.Clean(row.GetValueOrDefault("content") as string), row.GetValueOrDefault("summary") as string, DateOf(row) ?? DateOnly.FromDateTime((DateTime)row["creation"]!));
    }

    /// <summary>The published pages that want to be listed, newest first.</summary>
    public async Task<IReadOnlyList<PublicPageLink>> IndexAsync(CancellationToken ct = default)
    {
        var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery
        {
            Filters = [new("published", FilterOp.Eq, true), new("show_in_list", FilterOp.Eq, true)],
            Fields = ["name", "title", "summary", "published_on", "creation"], OrderBy = "creation", Descending = true, Limit = MaxIndex,
        }, ct);
        return rows.Select(r => new PublicPageLink((string)r["name"]!, r["title"] as string ?? (string)r["name"]!, r.GetValueOrDefault("summary") as string,
                DateOf(r) ?? (r.GetValueOrDefault("creation") is DateTime c ? DateOnly.FromDateTime(c) : null)))
            .OrderByDescending(p => p.PublishedOn).ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
