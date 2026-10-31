using System.Net;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Network rules per role: where a role may be used from.</summary>
public class RoleNetworkTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private static readonly UserContext Both = new("both@example.com", ["Clerk", "Auditor"]);
    private static readonly IPAddress Office = IPAddress.Parse("10.1.2.3");
    private static readonly IPAddress Home = IPAddress.Parse("203.0.113.9");

    private async Task RoleAsync(string name, string? networks)
    {
        var existing = await _engine.Repository.GetAsync(_engine.Registry.Get("Role"), name);
        if (existing is null)
            await _engine.Documents.InsertAsync("Role", Values(("role_name", name), ("allowed_networks", networks)), TestUsers.Admin);
        else
            await _engine.Documents.UpdateAsync("Role", name, Values(("allowed_networks", networks)), TestUsers.Admin);
    }

    [Fact]
    public async Task A_restricted_role_can_only_be_used_from_its_networks()
    {
        await RoleAsync("Clerk", "10.0.0.0/8\n192.168.1.5");

        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(Clerk, Office));
        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(Clerk, IPAddress.Parse("192.168.1.5")));
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Clerk, Home));
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Clerk, null)); // an unknown address is never inside
        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(new UserContext("other@example.com", ["Sales"]), Home)); // no rule on their roles
        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(UserContext.Anonymous, Home));
    }

    [Fact]
    public async Task Someone_with_several_restricted_roles_must_be_inside_all_of_them()
    {
        await RoleAsync("Clerk", "10.0.0.0/8");
        await RoleAsync("Auditor", "10.1.0.0/16, 172.16.0.0/12");

        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(Both, Office));
        Assert.Equal("Auditor", await _engine.RoleNetworks.BlockedByAsync(Both, IPAddress.Parse("10.9.9.9"))); // inside Clerk's, outside Auditor's
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Both, IPAddress.Parse("172.16.5.5"))); // inside Auditor's, outside Clerk's
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Clerk, IPAddress.Parse("172.16.5.5")));
    }

    [Fact]
    public async Task The_administrator_is_never_blocked_and_the_keeping_roles_cannot_be_restricted()
    {
        await RoleAsync("Clerk", "10.0.0.0/8");
        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(new UserContext("root@example.com", [UserContext.AdministratorRole, "Clerk"]), Home));

        foreach (var name in new[] { UserContext.AdministratorRole, "System Manager" })
        {
            var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Role", Values(("role_name", name), ("allowed_networks", "10.0.0.0/8")), TestUsers.Admin));
            Assert.Contains(ex.Errors, e => e.Field == "allowed_networks" && e.Message.Contains("lock everyone out"));
        }
    }

    [Fact]
    public async Task Entries_must_be_real_addresses_or_ranges()
    {
        async Task<string> Fails(string networks) =>
            (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Role", Values(("role_name", "Probe"), ("allowed_networks", networks)), TestUsers.Admin))).Errors[0].Message;

        Assert.Contains("is not an IP address", await Fails("office"));
        Assert.Contains("out of range", await Fails("10.0.0.0/99"));
        Assert.Contains("At most 50", await Fails(string.Join('\n', Enumerable.Range(1, 51).Select(i => $"10.0.{i}.0/24"))));
    }

    [Fact]
    public async Task Changes_apply_within_seconds_or_at_once_after_a_refresh()
    {
        await RoleAsync("Clerk", "10.0.0.0/8");
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Clerk, Home));

        await RoleAsync("Clerk", "203.0.113.0/24");
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Clerk, Home)); // still the rules read a moment ago
        _engine.Clock.Advance(RoleNetworkService.CacheFor + TimeSpan.FromSeconds(1));
        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(Clerk, Home));
        Assert.Equal("Clerk", await _engine.RoleNetworks.BlockedByAsync(Clerk, Office));

        await RoleAsync("Clerk", null); // restriction removed
        _engine.RoleNetworks.Refresh();
        Assert.Null(await _engine.RoleNetworks.BlockedByAsync(Clerk, Office));
    }
}
