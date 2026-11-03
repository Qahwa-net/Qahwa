using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Qahwa.Core;

namespace Qahwa.Api.Tests;

/// <summary>Apps that ship with the reference server are off until a site's own settings turn them on.</summary>
public class BundledAppsTests
{
    private sealed class Env : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Qahwa.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IEnumerable<IQahwaApp> AppsFor(params (string Key, string Value)[] settings)
    {
        var all = new Dictionary<string, string?> { ["ConnectionStrings:Default"] = "Host=localhost;Database=none;Username=x;Password=x" };
        foreach (var (key, value) in settings)
            all[key] = value;
        var config = new ConfigurationBuilder().AddInMemoryCollection(all).Build();
        var services = new ServiceCollection();
        services.AddQahwaSite(config, new Env(), null);
        return services.Where(d => d.ServiceType == typeof(IQahwaApp)).Select(d => (IQahwaApp)d.ImplementationInstance!);
    }

    [Fact]
    public void The_crm_app_is_off_by_default_and_on_when_the_site_says_so()
    {
        Assert.Empty(AppsFor());
        Assert.Empty(AppsFor(("Qahwa:Apps:Crm", "false")));
        Assert.Equal("qahwa_crm", Assert.Single(AppsFor(("Qahwa:Apps:Crm", "true"))).Name);
    }

    [Fact]
    public void The_crm_doctypes_travel_with_the_server_build()
    {
        var folder = new Qahwa.Crm.CrmApp().DocTypesPath!;
        Assert.True(File.Exists(Path.Combine(folder, "crm_lead.json")));
        Assert.True(File.Exists(Path.Combine(folder, "crm_opportunity.json")));
    }
}
