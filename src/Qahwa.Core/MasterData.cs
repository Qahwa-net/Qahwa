using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Qahwa.Core;

/// <summary>
/// Fills the lists every business system needs (currencies, countries, languages, salutations, genders) the first time the system starts,
/// as an ordinary one-time patch (see <see cref="IPatch"/>). Existing rows are left alone, so a changed or added row is never overwritten.
/// Afterwards they are normal documents that System Managers can add to and change.
/// </summary>
public sealed class MasterDataPatch : IPatch
{
    public string Name => "2026_03_01_seed_master_data";

    private static readonly UserContext System = new("Administrator", [UserContext.AdministratorRole]);

    private static readonly (string Code, string Name, bool Rtl)[] Languages =
    [
        ("en", "English", false), ("ar", "Arabic", true), ("fr", "French", false), ("de", "German", false), ("es", "Spanish", false), ("pt", "Portuguese", false),
        ("it", "Italian", false), ("nl", "Dutch", false), ("sv", "Swedish", false), ("da", "Danish", false), ("nb", "Norwegian", false), ("fi", "Finnish", false),
        ("pl", "Polish", false), ("cs", "Czech", false), ("tr", "Turkish", false), ("ru", "Russian", false), ("uk", "Ukrainian", false), ("el", "Greek", false),
        ("he", "Hebrew", true), ("fa", "Persian", true), ("ur", "Urdu", true), ("hi", "Hindi", false), ("bn", "Bengali", false), ("id", "Indonesian", false),
        ("ms", "Malay", false), ("th", "Thai", false), ("vi", "Vietnamese", false), ("zh", "Chinese", false), ("ja", "Japanese", false), ("ko", "Korean", false),
        ("sw", "Swahili", false), ("ro", "Romanian", false), ("hu", "Hungarian", false), ("bg", "Bulgarian", false),
    ];

    private static readonly string[] Salutations = ["Mr", "Mrs", "Ms", "Miss", "Dr", "Prof", "Eng"];
    private static readonly string[] Genders = ["Male", "Female", "Other", "Prefer not to say"];

    private sealed record CountryRow(string Code, string Name, string Currency);

    private sealed record CurrencyRow(string Code, string Name, string Symbol);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static List<T> Load<T>(string file)
    {
        var assembly = typeof(MasterDataPatch).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + file, StringComparison.Ordinal));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        return JsonSerializer.Deserialize<List<T>>(reader.ReadToEnd(), Json) ?? [];
    }

    /// <summary>Text without the invisible direction marks some systems put around symbols.</summary>
    private static string Clean(string text) => new string(text.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format && !char.IsControl(c)).ToArray()).Trim();

    public async Task RunAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var documents = services.GetRequiredService<DocumentService>();
        var repository = services.GetRequiredService<IDocumentRepository>();
        var registry = services.GetRequiredService<IMetaRegistry>();

        async Task AddAsync(string doctype, string name, Dictionary<string, object?> values)
        {
            if (await repository.ExistsAsync(registry.Get(doctype), name, ct))
                return;
            await documents.InsertAsync(doctype, values, System, ct);
        }

        // The role every signed-in person has, so the Role Permission Manager can use it.
        await AddAsync("Role", UserContext.AllRole, new() { ["role_name"] = UserContext.AllRole });

        foreach (var c in Load<CurrencyRow>("currencies.json"))
            await AddAsync("Currency", c.Code, new() { ["currency_code"] = c.Code, ["currency_name"] = Clean(c.Name), ["symbol"] = Clean(c.Symbol), ["enabled"] = true });
        foreach (var c in Load<CountryRow>("countries.json"))
            await AddAsync("Country", Clean(c.Name), new() { ["country_name"] = Clean(c.Name), ["code"] = c.Code, ["currency"] = c.Currency });
        foreach (var (code, name, rtl) in Languages)
            await AddAsync("Language", code, new() { ["language_code"] = code, ["language_name"] = name, ["is_rtl"] = rtl });
        foreach (var s in Salutations)
            await AddAsync("Salutation", s, new() { ["salutation"] = s });
        foreach (var g in Genders)
            await AddAsync("Gender", g, new() { ["gender"] = g });
    }
}
