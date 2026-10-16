using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class TranslationTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    private async Task AddAsync(string language, string source, string text)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Translation", Values(("language", language), ("source_text", source), ("translated_text", text)), TestUsers.SystemManager);
    }

    [Fact]
    public async Task Anyone_signed_in_reads_the_catalog_of_one_language()
    {
        await AddAsync("ar", "Save", "حفظ");
        await AddAsync("ar", "Cancel", "إلغاء");
        await AddAsync("fr", "Save", "Enregistrer");

        var arabic = await _engine.Translations.CatalogAsync("ar", Ann);

        Assert.Equal(2, arabic.Count);
        Assert.Equal("حفظ", arabic["Save"]);
        Assert.Equal(["ar", "fr"], await _engine.Translations.LanguagesAsync(Ann));
    }

    [Fact]
    public async Task A_later_translation_of_the_same_text_wins()
    {
        await AddAsync("fr", "Save", "Sauver");
        await AddAsync("fr", "Save", "Enregistrer");

        Assert.Equal("Enregistrer", (await _engine.Translations.CatalogAsync("fr", Ann))["Save"]);
    }

    [Fact]
    public async Task Only_a_system_manager_edits_translations()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.InsertAsync("Translation", Values(("language", "ar"), ("source_text", "Save"), ("translated_text", "x")), Ann));
    }

    [Fact]
    public async Task Guests_are_refused_and_hostile_language_codes_are_not_looked_up()
    {
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Translations.CatalogAsync("ar", UserContext.Anonymous));
        foreach (var bad in new[] { "", "ar'; drop table x; --", "../etc", "arabic-language-long", "A", "ar_", "ar<script>" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Translations.CatalogAsync(bad, Ann));
    }

    [Fact]
    public async Task Text_with_markup_is_returned_as_plain_data_and_not_changed()
    {
        await AddAsync("fr", "<b>Hi</b>", "<img src=x onerror=alert(1)>");

        Assert.Equal("<img src=x onerror=alert(1)>", (await _engine.Translations.CatalogAsync("fr", Ann))["<b>Hi</b>"]); // the Desk renders it as text, never as HTML
    }

    [Theory]
    [InlineData("ar", true)]
    [InlineData("ar-SA", true)]
    [InlineData("he", true)]
    [InlineData("en", false)]
    [InlineData("fr-CA", false)]
    public void Right_to_left_languages_are_known(string code, bool rtl) => Assert.Equal(rtl, TranslationService.IsRightToLeft(code));
}
