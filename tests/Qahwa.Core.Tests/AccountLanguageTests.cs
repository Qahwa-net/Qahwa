using Qahwa.Testing;

namespace Qahwa.Core.Tests;

/// <summary>The language of the Desk a person chose, kept on their account.</summary>
public class AccountLanguageTests
{
    private const string User = "ann@example.com";

    private readonly TestEngine _engine = new();

    private async Task SeedAsync() => await _engine.Auth.EnsureUserAsync(User, "Ann", TestUsers.StrongPassword, ["Clerk"]);

    [Fact]
    public async Task A_language_is_saved_read_back_and_cleared()
    {
        await SeedAsync();
        Assert.Null(await _engine.Auth.LanguageOfAsync(User));

        await _engine.Auth.SetLanguageAsync(User, " ar ");
        Assert.Equal("ar", await _engine.Auth.LanguageOfAsync(User));
        Assert.Equal("ar", await _engine.Auth.LanguageOfAsync("ANN@example.com")); // however the email is typed

        await _engine.Auth.SetLanguageAsync(User, "pt-BR");
        Assert.Equal("pt-BR", await _engine.Auth.LanguageOfAsync(User));

        await _engine.Auth.SetLanguageAsync(User, null);
        Assert.Null(await _engine.Auth.LanguageOfAsync(User));
    }

    [Theory]
    [InlineData("english")]
    [InlineData("a")]
    [InlineData("<script>")]
    [InlineData("ar; drop")]
    [InlineData("aaaa-bbbbbbbbbbbbbbbb")]
    public async Task Only_a_short_plain_language_code_is_accepted(string code)
    {
        await SeedAsync();
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Auth.SetLanguageAsync(User, code));
        Assert.Contains(ex.Errors, e => e.Field == "language");
        Assert.Null(await _engine.Auth.LanguageOfAsync(User));
    }

    [Fact]
    public async Task Someone_who_does_not_exist_cannot_have_a_language()
    {
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Auth.SetLanguageAsync("nobody@example.com", "ar"));
        Assert.Null(await _engine.Auth.LanguageOfAsync("nobody@example.com"));
        Assert.Null(await _engine.Auth.LanguageOfAsync(null!));
    }

    [Fact]
    public async Task The_language_is_not_visible_or_editable_through_the_document_api()
    {
        await SeedAsync();
        await _engine.Auth.SetLanguageAsync(User, "ar");

        var seenByManager = await _engine.Documents.GetAsync("User", User, new UserContext("m@example.com", ["System Manager"]));

        Assert.False(seenByManager.ContainsKey("language")); // permission level 9
        Assert.Equal("ar", (await _engine.Documents.GetAsync("User", User, TestUsers.Admin))["language"]);
    }
}
