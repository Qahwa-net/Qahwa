using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AppUninstallTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    private sealed class DemoApp(string name, int[] counter, params string[] needs) : IQahwaApp
    {
        public string Name => name;
        public string Version => "1.0.0";
        public IReadOnlyList<string> Dependencies => needs;

        public void Configure(IAppBuilder app) => app
            .Method("ping", _ => Task.FromResult<object?>("pong"))
            .Schedule("tick", TimeSpan.FromMinutes(1), (_, _) => { counter[0]++; return Task.CompletedTask; })
            .Boot(_ => Task.FromResult<object?>(new { name }))
            .Fixture("Marker", new Dictionary<string, object?> { ["label"] = name });
    }

    public AppUninstallTests() =>
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Marker", "autoname": "field:label", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true } ] }"""));

    private async Task<int[]> InstallAsync(params (string Name, string[] Needs)[] apps)
    {
        var counter = new int[1];
        foreach (var (name, needs) in apps)
            _engine.AppCatalog.Add(new DemoApp(name, counter, needs));
        await _engine.AppInstaller.InstallAllAsync();
        return counter;
    }

    [Fact]
    public async Task An_uninstalled_app_stops_its_methods_jobs_and_boot_data_but_keeps_its_data()
    {
        var ticks = await InstallAsync(("shop", []));
        Assert.Equal("pong", (await _engine.Methods.InvokeAsync("shop.ping", default, Ann, false, default))?.ToString());

        await _engine.AppInstaller.UninstallAsync("shop");

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Methods.InvokeAsync("shop.ping", default, Ann, false, default));
        await _engine.AppJobs.RunDueAsync();
        Assert.Equal(0, ticks[0]);
        Assert.Empty(await _engine.Boot.CollectAsync(Ann));
        Assert.False((await _engine.AppInstaller.ListAsync()).Single().Installed);
        Assert.Single(await _engine.Repository.ListAsync(_engine.Registry.Get("Marker"), new ListQuery { Limit = 5 })); // the fixture stays
    }

    [Fact]
    public async Task Installing_again_switches_it_back_on_without_making_the_fixtures_twice_and_a_restart_keeps_it_off()
    {
        await InstallAsync(("shop", []));
        await _engine.AppInstaller.UninstallAsync("shop");

        await _engine.AppInstaller.InstallAllAsync(); // a server start does not bring it back
        Assert.False(_engine.AppCatalog.IsActive("shop"));

        await _engine.AppInstaller.InstallAsync("shop");

        Assert.True(_engine.AppCatalog.IsActive("shop"));
        Assert.Equal("shop", (await _engine.Boot.CollectAsync(Ann))["shop"].GetProperty("name").GetString());
        Assert.Single(await _engine.Repository.ListAsync(_engine.Registry.Get("Marker"), new ListQuery { Limit = 5 }));
    }

    [Fact]
    public async Task An_app_that_others_need_cannot_be_uninstalled_and_cannot_be_installed_without_what_it_needs()
    {
        await InstallAsync(("base", []), ("shop", ["base"]));

        var refused = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AppInstaller.UninstallAsync("base"));
        Assert.Contains("shop", refused.Errors[0].Message);

        await _engine.AppInstaller.UninstallAsync("shop");
        await _engine.AppInstaller.UninstallAsync("base");
        var needs = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.AppInstaller.InstallAsync("shop"));
        Assert.Contains("base", needs.Errors[0].Message);
        await _engine.AppInstaller.InstallAsync("base");
        await _engine.AppInstaller.InstallAsync("shop");
        Assert.True(_engine.AppCatalog.IsActive("shop") && _engine.AppCatalog.IsActive("base"));
    }

    [Fact]
    public async Task A_change_made_elsewhere_is_picked_up_on_refresh_and_takes_the_apps_that_need_it_with_it()
    {
        await InstallAsync(("base", []), ("shop", ["base"]));

        // As the command line does: the status of the record changes, no server code runs.
        var meta = _engine.Registry.Get("Installed App");
        var row = (await _engine.Repository.GetAsync(meta, "base"))!;
        await _engine.Repository.UpdateAsync(meta, "base", new Dictionary<string, object?> { ["status"] = "Uninstalled", ["modified"] = DateTime.UtcNow.AddMinutes(1) }, (DateTime)row["modified"]!);

        Assert.True(await _engine.AppInstaller.RefreshAsync());
        Assert.False(_engine.AppCatalog.IsActive("base"));
        Assert.False(_engine.AppCatalog.IsActive("shop")); // it needs base
        Assert.False(await _engine.AppInstaller.RefreshAsync()); // nothing new
    }

    [Fact]
    public async Task Unknown_apps_are_reported_and_repeating_an_action_is_harmless()
    {
        await InstallAsync(("shop", []));

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AppInstaller.UninstallAsync("ghost"));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AppInstaller.InstallAsync("ghost"));
        await _engine.AppInstaller.InstallAsync("shop");
        await _engine.AppInstaller.UninstallAsync("shop");
        await _engine.AppInstaller.UninstallAsync("shop");
        Assert.False((await _engine.AppInstaller.ListAsync()).Single().Installed);
    }
}
