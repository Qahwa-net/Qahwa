namespace Qahwa.Core;

public sealed record SearchDocType(string Name, string? Module, bool CanCreate);

public sealed record SearchHit(string DocType, string Name, string? Title);

public sealed record SearchResults(IReadOnlyList<SearchDocType> DocTypes, IReadOnlyList<SearchHit> Documents);

/// <summary>
/// The search box that finds DocTypes and documents across the system. Documents are found by the same reads as a list (so a person
/// only ever sees what they may see: permissions, own-only rules, shares, User Permissions and blocked modules all apply), by title
/// (or name when a DocType has no title field) and, when the text looks like a document name, by name. The text is only ever a
/// literal to look for: the characters that mean something in a pattern are escaped. The number of DocTypes asked, results per DocType
/// and total, and searches per minute are limited.
/// </summary>
public sealed class GlobalSearchService(DocumentService documents, PermissionService permissions, HitCounter hits)
{
    public const int MaxQueryLength = 100;
    public const int MaxDocTypesAsked = 60;
    public const int PerDocType = 5;
    public const int MaxDocuments = 30;
    public const int MaxDocTypeMatches = 12;
    public const int PerMinute = 120;

    /// <summary>Escapes the characters that stand for something in a pattern, so the text is looked for as it is.</summary>
    public static string Literal(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal);

    public async Task<SearchResults> SearchAsync(string? text, IUserContext user, CancellationToken ct = default)
    {
        var q = (text ?? string.Empty).Trim();
        if (q.Length == 0)
            return new SearchResults([], []);
        if (q.Length > MaxQueryLength || q.Any(char.IsControl))
            throw new DocumentValidationException([new FieldError("q", $"Search for at most {MaxQueryLength} characters of plain text.")]);
        if (!hits.TryHit($"search:{user.User}", PerMinute, TimeSpan.FromMinutes(1)))
            throw new RateLimitedException();

        var metas = await documents.ListMetasAsync(user, ct);
        var matched = metas
            .Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxDocTypeMatches)
            .Select(m => new SearchDocType(m.Name, m.Module, permissions.Evaluate(m, user, PermAction.Create) != AccessLevel.None && !m.IsSingle))
            .ToList();

        var pattern = "%" + Literal(q) + "%";
        var looksLikeAName = q.Any(char.IsDigit) || q.Contains('-');
        var found = new List<SearchHit>();
        foreach (var meta in metas.Where(m => !m.IsSingle && m.Module != "Core").Take(MaxDocTypesAsked))
        {
            if (found.Count >= MaxDocuments)
                break;
            foreach (var field in Fields(meta, looksLikeAName))
            {
                try
                {
                    var page = await documents.ListAsync(meta.Name, new ListRequest
                    {
                        Filters = [new RawFilter(field, "like", pattern)],
                        Fields = meta.TitleField is { } t && t != "name" ? ["name", t] : ["name"],
                        OrderBy = "modified", Descending = true, Limit = PerDocType,
                    }, user, ct);
                    foreach (var row in page.Data)
                    {
                        var name = (string)row["name"]!;
                        if (found.Any(h => h.DocType == meta.Name && h.Name == name) || found.Count >= MaxDocuments)
                            continue;
                        found.Add(new SearchHit(meta.Name, name, meta.TitleField is { } tf && tf != "name" ? row.GetValueOrDefault(tf) as string : null));
                    }
                }
                catch (Exception e) when (e is DocumentValidationException or PermissionDeniedException)
                {
                    // A DocType whose title the person may not read, or that they may not open, simply has nothing to show.
                }
            }
        }

        return new SearchResults(matched, found);
    }

    private static IEnumerable<string> Fields(DocTypeMeta meta, bool alsoByName)
    {
        if (meta.TitleField is { Length: > 0 } title && title != "name")
        {
            yield return title;
            if (alsoByName)
                yield return "name";
        }
        else
        {
            yield return "name";
        }
    }
}
