using Microsoft.Extensions.DependencyInjection;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class MasterDataTests
{
    private readonly TestEngine _engine = new();

    private IServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_engine.Documents);
        services.AddSingleton(_engine.Repository);
        services.AddSingleton<IMetaRegistry>(_engine.Registry);
        return services.BuildServiceProvider();
    }

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk", UserContext.AllRole]);

    private async Task SeedAsync() => await new MasterDataPatch().RunAsync(Services());

    private async Task<long> CountAsync(string doctype) =>
        (await _engine.Documents.ListAsync(doctype, new ListRequest { Fields = ["name"], Limit = 1 }, TestUsers.Admin)).Total;

    [Fact]
    public async Task The_lists_are_filled_in_and_the_countries_point_at_real_currencies()
    {
        await SeedAsync();

        Assert.True(await CountAsync("Country") > 200);
        Assert.True(await CountAsync("Currency") > 100);
        Assert.True(await CountAsync("Language") >= 30);
        Assert.Equal(7, await CountAsync("Salutation"));
        Assert.Equal(4, await CountAsync("Gender"));
        var saudi = await _engine.Documents.GetAsync("Country", "Saudi Arabia", TestUsers.Admin);
        Assert.Equal("SA", saudi["code"]);
        Assert.Equal("SAR", saudi["currency"]);
        Assert.Equal(true, (await _engine.Documents.GetAsync("Language", "ar", TestUsers.Admin))["is_rtl"]);
        Assert.Equal(false, (await _engine.Documents.GetAsync("Language", "en", TestUsers.Admin))["is_rtl"]);
        Assert.Contains("SAR", (string)(await _engine.Documents.GetAsync("Currency", "SAR", TestUsers.Admin))["currency_code"]!);
        Assert.DoesNotContain('‏', (string)(await _engine.Documents.GetAsync("Currency", "SAR", TestUsers.Admin))["symbol"]!); // no hidden direction marks
        Assert.NotNull(await _engine.Documents.GetAsync("Role", "All", TestUsers.Admin));
    }

    [Fact]
    public async Task Running_it_again_adds_nothing_and_never_overwrites_a_changed_row()
    {
        await SeedAsync();
        var before = await CountAsync("Country");
        await _engine.Documents.UpdateAsync("Language", "ar", Values(("language_name", "Arabic (changed)")), TestUsers.SystemManager);

        await SeedAsync();

        Assert.Equal(before, await CountAsync("Country"));
        Assert.Equal("Arabic (changed)", (await _engine.Documents.GetAsync("Language", "ar", TestUsers.Admin))["language_name"]);
    }

    [Fact]
    public async Task Everyone_signed_in_can_read_but_only_a_system_manager_can_change()
    {
        await SeedAsync();

        Assert.Equal("SAR", (await _engine.Documents.GetAsync("Country", "Saudi Arabia", Ann))["currency"]);
        var found = await _engine.Documents.SearchLinkAsync("Country", "saudi", 5, Ann);
        Assert.Equal(["Saudi Arabia"], found.Select(o => o.Value));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Country", Values(("country_name", "Atlantis"), ("code", "AT2")), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Language", "ar", Values(("language_name", "x")), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Gender", "Male", Ann));
        await _engine.Documents.InsertAsync("Country", Values(("country_name", "Atlantis"), ("code", "ZZ")), TestUsers.SystemManager);
    }

    [Fact]
    public async Task Someone_without_a_session_gets_nothing_and_a_made_up_currency_is_refused()
    {
        await SeedAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Country", "Saudi Arabia", new UserContext("x@example.com", ["Clerk"]))); // not signed in through a session: no All role
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Country", Values(("country_name", "Bad"), ("code", "BB"), ("currency", "NOPE")), TestUsers.SystemManager));
    }

    [Fact]
    public async Task Every_signed_in_session_has_the_All_role_and_it_cannot_be_used_to_become_an_administrator()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann", "a-long-passphrase-9", ["Clerk"]);

        var session = (await _engine.Auth.LoadSessionAsync("ann@example.com"))!;

        Assert.Contains(UserContext.AllRole, session.User.Roles);
        Assert.DoesNotContain(UserContext.AdministratorRole, session.User.Roles);
        Assert.Contains("Clerk", session.User.Roles);
    }

    [Fact]
    public async Task Countries_have_dialling_codes_and_older_rows_get_them_from_a_patch_without_overwriting()
    {
        await SeedAsync();
        var saudi = await _engine.Documents.GetAsync("Country", "Saudi Arabia", Ann);
        Assert.Equal("966", saudi["dial_code"]);
        Assert.Equal("1", (await _engine.Documents.GetAsync("Country", "United States", Ann))["dial_code"]);

        // A country seeded before the field existed (no code) and one an administrator changed on purpose.
        var countries = _engine.Registry.Get("Country");
        await _engine.Repository.UpdateAsync(countries, "France", new Dictionary<string, object?> { ["dial_code"] = null }, null);
        await _engine.Repository.UpdateAsync(countries, "Germany", new Dictionary<string, object?> { ["dial_code"] = "999" }, null);

        await new CountryDialCodePatch().RunAsync(Services());

        Assert.Equal("33", (await _engine.Documents.GetAsync("Country", "France", Ann))["dial_code"]);
        Assert.Equal("999", (await _engine.Documents.GetAsync("Country", "Germany", Ann))["dial_code"]);
    }
}
