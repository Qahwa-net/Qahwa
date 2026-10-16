using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public class AppDependencyTests
{
    private sealed class DepApp(string name, params string[] needs) : IQahwaApp
    {
        public string Name => name;
        public string Version => "1.0.0";
        public IReadOnlyList<string> Dependencies => needs;
        public void Configure(IAppBuilder app) { }
    }

    [Fact]
    public void Apps_are_installed_after_the_apps_they_need_and_otherwise_in_the_order_added()
    {
        var catalog = new AppCatalog([new DepApp("shop", "billing"), new DepApp("billing", "core_pack"), new DepApp("core_pack"), new DepApp("blog")]);

        Assert.Equal(["core_pack", "billing", "shop", "blog"], catalog.InstallOrder().Select(a => a.Name));
    }

    [Fact]
    public void A_missing_dependency_or_a_circle_is_reported_with_the_names_involved()
    {
        var missing = Assert.Throws<InvalidOperationException>(() => new AppCatalog([new DepApp("shop", "ghost")]).InstallOrder());
        Assert.Contains("'shop'", missing.Message);
        Assert.Contains("'ghost'", missing.Message);

        var circle = Assert.Throws<InvalidOperationException>(() => new AppCatalog([new DepApp("aa", "bb"), new DepApp("bb", "cc"), new DepApp("cc", "aa")]).InstallOrder());
        Assert.Contains("circle", circle.Message);
        Assert.Throws<InvalidOperationException>(() => new AppCatalog([new DepApp("selfish", "selfish")]).InstallOrder());
    }

    [Fact]
    public void Validation_at_start_up_checks_dependencies()
    {
        var engine = new TestEngine();
        engine.AppCatalog.Add(new DepApp("shop", "ghost"));

        Assert.Throws<InvalidOperationException>(() => engine.AppCatalog.Validate(engine.Registry));
    }

    [Fact]
    public async Task Installing_records_every_app_even_when_the_one_that_needs_another_was_added_first()
    {
        var engine = new TestEngine();
        engine.AppCatalog.Add(new DepApp("later", "base"));
        engine.AppCatalog.Add(new DepApp("base"));

        await engine.AppInstaller.InstallAllAsync();

        Assert.Equal(["base", "later"], (await engine.AppInstaller.ListAsync()).Select(a => a.Name).Order());
    }
}
