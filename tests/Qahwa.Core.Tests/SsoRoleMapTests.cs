using Qahwa.Core.Auth;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class SsoRoleMapTests
{
    private const string Redirect = "https://qahwa.example.com/api/auth/sso/idp/callback";
    private const string Password = "a-long-passphrase-9";

    private readonly TestEngine _engine = new();

    private static string Query(string url, string name) =>
        Uri.UnescapeDataString(url[(url.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1)..].Split('&')[0]);

    private SsoProvider Provider => _engine.SsoOptions.Providers["idp"];

    private void Map(string claim = "groups", params (string Group, string[] Roles)[] map)
    {
        Provider.GroupsClaim = claim;
        Provider.RoleMap = map.ToDictionary(m => m.Group, m => m.Roles, StringComparer.Ordinal);
    }

    private async Task<string> SignInAsync(string email, object? groups, string claim = "groups")
    {
        var start = await _engine.Sso.StartAsync("idp", Redirect, "/");
        _engine.IdentityProvider.NextToken = _engine.IdentityProvider.Issue("sub-" + email, email, true, "Ann Lee", Query(start.Url, "nonce"), groupsClaim: claim, groups: groups);
        return (await _engine.Sso.CompleteAsync("idp", Redirect, start.State, "code-1", Query(start.Url, "state"))).User;
    }

    private async Task<string[]> RolesAsync(string email) => (await _engine.Auth.LoadSessionAsync(email))!.User.Roles.Where(r => r != "All").Order().ToArray();

    [Fact]
    public async Task Roles_follow_the_groups_at_every_sign_in_and_only_the_mapped_roles_are_touched()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Password, ["Clerk", "Support"]);
        Map("groups", ("staff", ["Support", "Employee"]), ("sales", ["Seller"]));

        await SignInAsync("ann@example.com", new[] { "staff", "other" });
        Assert.Equal(["Clerk", "Employee", "Support"], await RolesAsync("ann@example.com"));

        await SignInAsync("ann@example.com", new[] { "sales" });
        Assert.Equal(["Clerk", "Seller"], await RolesAsync("ann@example.com")); // Support and Employee taken away, Clerk (not mapped) kept

        await SignInAsync("ann@example.com", Array.Empty<string>());
        Assert.Equal(["Clerk"], await RolesAsync("ann@example.com"));
    }

    [Fact]
    public async Task The_groups_can_arrive_as_a_list_or_as_one_text_and_a_missing_claim_means_no_groups()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Password, []);
        Map("roles", ("a", ["RoleA"]), ("b", ["RoleB"]));
        await SignInAsync("ann@example.com", "a, b", claim: "roles");
        Assert.Equal(["RoleA", "RoleB"], await RolesAsync("ann@example.com"));
        await SignInAsync("ann@example.com", null, claim: "roles");
        Assert.Empty(await RolesAsync("ann@example.com"));
    }

    [Theory]
    [InlineData("System Manager")]
    [InlineData("Administrator")]
    [InlineData("administrator")]
    [InlineData("All")]
    [InlineData("Guest")]
    public async Task A_provider_can_never_be_given_the_power_to_hand_out_the_top_roles(string role)
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Password, []);
        Map("groups", ("admins", [role]));
        var ex = await Assert.ThrowsAsync<SsoException>(() => _engine.Sso.StartAsync("idp", Redirect, "/"));
        Assert.Equal("misconfigured", ex.Code);
        Assert.Empty(await RolesAsync("ann@example.com"));
    }

    [Fact]
    public async Task Without_a_role_map_the_token_never_gives_roles()
    {
        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Password, ["Clerk"]);
        await SignInAsync("ann@example.com", new[] { "System Manager", "Administrator", "admins" });
        Assert.Equal(["Clerk"], await RolesAsync("ann@example.com"));
    }

    [Fact]
    public async Task Half_a_map_is_refused_and_odd_groups_are_ignored()
    {
        Provider.GroupsClaim = "groups";
        Provider.RoleMap = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await Assert.ThrowsAsync<SsoException>(() => _engine.Sso.StartAsync("idp", Redirect, "/"));

        await _engine.Auth.EnsureUserAsync("ann@example.com", "Ann Lee", Password, []);
        Map("groups", ("ok", ["RoleA"]));
        await SignInAsync("ann@example.com", new object[] { 5, new string('g', 500), "ok" });
        Assert.Equal(["RoleA"], await RolesAsync("ann@example.com"));
    }

    [Fact]
    public async Task A_person_in_a_mapped_group_who_has_the_top_role_keeps_it()
    {
        await _engine.Auth.EnsureUserAsync("boss@example.com", "Boss", Password, ["System Manager", "Support"]);
        Map("groups", ("staff", ["Support"]));
        await SignInAsync("boss@example.com", Array.Empty<string>());
        Assert.Equal(["System Manager"], await RolesAsync("boss@example.com")); // Support is taken away, the top role is never touched
    }
}
