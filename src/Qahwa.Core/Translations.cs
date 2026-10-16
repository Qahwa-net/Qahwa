using System.Text.RegularExpressions;

namespace Qahwa.Core;

/// <summary>
/// Translations of the Desk's words and of DocType and field labels, kept as ordinary Translation documents so a System Manager can
/// edit them like any other data. Everyone who is signed in can read the catalog of a language (it is interface text, not data).
/// </summary>
public sealed partial class TranslationService(IMetaRegistry registry, IDocumentRepository repository)
{
    public const string DocTypeName = "Translation";
    public const int MaxEntries = 5000;

    /// <summary>Languages written right to left.</summary>
    public static readonly IReadOnlySet<string> RightToLeft = new HashSet<string>(StringComparer.Ordinal) { "ar", "he", "fa", "ur", "ps", "sd", "yi" };

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex LanguagePattern();

    public static bool IsLanguage(string? code) => code is { Length: <= 12 } && LanguagePattern().IsMatch(code);

    public static bool IsRightToLeft(string code) => RightToLeft.Contains(code.Split('-')[0]);

    /// <summary>Source text to translated text for one language. When a text was translated twice, the later translation wins.</summary>
    public async Task<IReadOnlyDictionary<string, string>> CatalogAsync(string language, IUserContext user, CancellationToken ct = default)
    {
        Require(user);
        if (!IsLanguage(language))
            throw new DocumentValidationException("That is not a language code (use letters such as 'ar' or 'pt-BR').");

        var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery
        {
            Filters = [new QueryFilter("language", FilterOp.Eq, language)],
            Fields = ["source_text", "translated_text"],
            OrderBy = "modified", Descending = false, Limit = MaxEntries,
        }, ct);

        var catalog = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in rows)
            if (row["source_text"] is string source && row["translated_text"] is string text && source.Length > 0 && text.Length > 0)
                catalog[source] = text;
        return catalog;
    }

    /// <summary>The languages that have at least one translation.</summary>
    public async Task<IReadOnlyList<string>> LanguagesAsync(IUserContext user, CancellationToken ct = default)
    {
        Require(user);
        var rows = await repository.ListAsync(registry.Get(DocTypeName), new ListQuery { Fields = ["language"], OrderBy = "language", Descending = false, Limit = MaxEntries }, ct);
        return rows.Select(r => r["language"] as string).Where(IsLanguage).Select(l => l!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    private static void Require(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
    }
}
