using System.Net;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class SystemSettingsTests
{
    private readonly TestEngine _engine = new();

    private SystemSettingsService Settings => new(_engine.Registry, _engine.Repository);

    private Task SaveAsync(params (string, object?)[] values) =>
        _engine.Documents.UpdateAsync("System Settings", "System Settings", Values(values), TestUsers.SystemManager);

    [Fact]
    public async Task Without_anything_saved_the_defaults_apply()
    {
        Assert.Equal(SystemSettingsService.Defaults, await Settings.GetAsync());
    }

    [Fact]
    public async Task Saved_settings_are_read_back()
    {
        await SaveAsync(("time_zone", "Asia/Riyadh"), ("date_format", "dd/mm/yyyy"), ("first_day_of_week", "Saturday"), ("default_language", "ar"));

        Assert.Equal(new DisplaySettings("Asia/Riyadh", "dd/mm/yyyy", "Saturday", "ar"), await Settings.GetAsync());
    }

    [Fact]
    public async Task A_value_that_is_not_valid_falls_back_instead_of_breaking_the_pages()
    {
        await SaveAsync(("time_zone", "Mars/Olympus"), ("default_language", "<script>"));

        var settings = await Settings.GetAsync();

        Assert.Equal("UTC", settings.TimeZone);
        Assert.Equal("en", settings.DefaultLanguage);
    }

    [Fact]
    public async Task Only_a_system_manager_can_change_them()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.UpdateAsync("System Settings", "System Settings", Values(("time_zone", "UTC")), new UserContext("ann@example.com", ["Clerk"])));
    }

    [Theory]
    [InlineData("UTC", true)]
    [InlineData("Asia/Riyadh", true)]
    [InlineData("", false)]
    [InlineData("../../etc/passwd", false)]
    [InlineData("Not/AZone", false)]
    [InlineData("UTC; drop table x", false)]
    public void Time_zones_are_checked(string id, bool ok) => Assert.Equal(ok, SystemSettingsService.IsTimeZone(id));
}

public class IpAllowListTests
{
    private static bool Allows(string[] list, string? address) => IpAllowList.Parse(list).Allows(address is null ? null : IPAddress.Parse(address));

    [Fact]
    public void No_list_means_anywhere()
    {
        Assert.True(IpAllowList.Parse(null).IsEmpty);
        Assert.True(IpAllowList.Parse([" ", ""]).Allows(IPAddress.Parse("8.8.8.8")));
        Assert.True(IpAllowList.None.Allows(null));
    }

    [Theory]
    [InlineData("203.0.113.5", true)]
    [InlineData("203.0.113.6", false)]
    [InlineData("10.20.30.40", true)]
    [InlineData("11.0.0.1", false)]
    [InlineData("192.168.1.77", true)]
    [InlineData("192.168.2.1", false)]
    [InlineData("::ffff:203.0.113.5", true)] // an IPv4 address seen through IPv6
    [InlineData("2001:db8:1::1", true)]
    [InlineData("2001:db9::1", false)]
    public void Addresses_and_ranges_are_matched(string address, bool expected) =>
        Assert.Equal(expected, Allows(["203.0.113.5", "10.0.0.0/8", "192.168.1.0/24", "2001:db8::/32"], address));

    [Fact]
    public void An_unknown_address_is_refused_once_a_list_exists_and_odd_prefixes_work()
    {
        Assert.False(Allows(["10.0.0.0/8"], null));
        Assert.True(Allows(["172.16.0.0/12"], "172.31.255.255"));
        Assert.False(Allows(["172.16.0.0/12"], "172.32.0.1"));
        Assert.True(Allows(["0.0.0.0/0"], "8.8.8.8"));
        Assert.True(Allows(["203.0.113.5/32"], "203.0.113.5"));
        Assert.False(Allows(["203.0.113.5/32"], "203.0.113.4"));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("10.0.0.0/x")]
    [InlineData("300.1.1.1")]
    public void A_typo_stops_the_server_instead_of_quietly_opening_or_closing_it(string entry) =>
        Assert.Throws<FormatException>(() => IpAllowList.Parse([entry]));
}
