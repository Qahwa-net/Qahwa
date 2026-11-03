using Qahwa.Core;
using Qahwa.Testing;

namespace MyApp.Tests;

public class AppModuleTests
{
    [Fact]
    public void The_app_has_a_valid_name_and_its_DocTypes_load()
    {
        var app = new QahwaAppModule();
        Assert.Matches("^[a-z][a-z0-9_]{1,30}$", app.Name);

        var folder = Path.Combine(AppContext.BaseDirectory, "DocTypes");
        var files = Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var file in files)
            Assert.NotNull(DocTypeLoader.Parse(File.ReadAllText(file)));
    }

    [Fact]
    public async Task An_item_can_be_saved_through_the_document_engine()
    {
        var engine = new TestEngine();
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "DocTypes"), "*.json", SearchOption.AllDirectories))
            engine.Registry.Add(DocTypeLoader.Parse(File.ReadAllText(file)));

        var item = await engine.Documents.InsertAsync("DOCTYPE_PREFIX Item", new Dictionary<string, object?> { ["title"] = "First" }, TestUsers.Admin);

        Assert.Equal("Open", item["status"]); // the DocType's default
        Assert.StartsWith("ITEM-", (string)item["name"]!);
    }
}
