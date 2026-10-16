using Qahwa.Core.Designer;
using Qahwa.Testing;

namespace Qahwa.Core.Tests;

public sealed class DeveloperModeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "qahwa-dev-" + Guid.NewGuid().ToString("N"));
    private readonly TestEngine _engine = new();

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private DeveloperExport Export(bool enabled = true) => new(new DeveloperOptions { Enabled = enabled, AppPath = _folder });

    [Theory]
    [InlineData("Sales Order", "sales_order.json")]
    [InlineData("Customer", "customer.json")]
    [InlineData("  Odd--Name!! 2 ", "odd_name_2.json")]
    [InlineData("مقهى Cafe", "cafe.json")]
    public void File_names_come_only_from_letters_and_digits(string name, string expected) => Assert.Equal(expected, DeveloperExport.FileNameFor(name));

    [Theory]
    [InlineData("")]
    [InlineData("!!!")]
    [InlineData("مقهى")]
    public void A_name_with_nothing_usable_is_refused(string name) => Assert.Throws<ArgumentException>(() => DeveloperExport.FileNameFor(name));

    [Fact]
    public async Task A_definition_is_written_whole_and_replaced_and_removed_with_the_doctype()
    {
        var export = Export();

        await export.ChangedAsync("Sales Order", "{ \"name\": \"Sales Order\" }");
        var path = Path.Combine(_folder, "sales_order.json");
        Assert.Equal("{ \"name\": \"Sales Order\" }" + Environment.NewLine, await File.ReadAllTextAsync(path));
        await export.ChangedAsync("Sales Order", "{ \"name\": \"Sales Order\", \"v\": 2 }");
        Assert.Contains("\"v\": 2", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp")); // no half-written leftovers

        await export.ChangedAsync("Sales Order", null);
        Assert.False(File.Exists(path));
        await export.ChangedAsync("Sales Order", null); // already gone: fine
    }

    [Fact]
    public async Task Nothing_is_written_when_it_is_off_and_a_path_cannot_leave_the_folder()
    {
        await Export(enabled: false).ChangedAsync("Customer", "{}");
        Assert.False(Directory.Exists(_folder));

        // The name is reduced to letters and digits, so no name can climb out of the folder.
        await Export().ChangedAsync("../../evil", "{}");
        Assert.Equal([Path.Combine(_folder, "evil.json")], Directory.GetFiles(_folder));
    }

    [Fact]
    public void The_settings_must_name_a_folder_when_on()
    {
        Assert.Contains("AppPath", DeveloperExport.Problem(new DeveloperOptions { Enabled = true }));
        Assert.Null(DeveloperExport.Problem(new DeveloperOptions { Enabled = true, AppPath = "x" }));
        Assert.Null(DeveloperExport.Problem(new DeveloperOptions()));
        Assert.Throws<InvalidOperationException>(() => new DeveloperExport(new DeveloperOptions()).WriteAllAsync([]).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task The_designer_writes_the_file_after_each_change_and_a_failing_file_never_undoes_the_change()
    {
        var designer = new DocTypeDesigner(_engine.Registry, _engine.DocTypeStore, _engine.Schema, _engine.Repository, _engine.Clock, Export());
        var definition = DocTypeLoader.Parse("""{ "name": "Gizmo", "autoname": "GZ-.####", "module": "Buying", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Sales Manager", "read": true } ] }""");

        await designer.CreateAsync(definition, TestUsers.Admin);
        var path = Path.Combine(_folder, "gizmo.json");
        Assert.Contains("\"Gizmo\"", await File.ReadAllTextAsync(path));

        var changed = DocTypeLoader.Parse("""{ "name": "Gizmo", "autoname": "GZ-.####", "module": "Buying", "fields": [ { "name": "label", "type": "Data" }, { "name": "extra", "type": "Data" } ], "permissions": [ { "role": "Sales Manager", "read": true } ] }""");
        await designer.UpdateAsync("Gizmo", changed, null, TestUsers.Admin);
        Assert.Contains("\"extra\"", await File.ReadAllTextAsync(path));

        await designer.DeleteAsync("Gizmo", TestUsers.Admin);
        Assert.False(File.Exists(path));

        // A folder that cannot be written to: the DocType is still made.
        var blocked = Path.Combine(_folder, "blocked");
        await File.WriteAllTextAsync(Path.Combine(_folder, "blocked"), "a file where the folder should be");
        var broken = new DocTypeDesigner(_engine.Registry, _engine.DocTypeStore, _engine.Schema, _engine.Repository, _engine.Clock, new DeveloperExport(new DeveloperOptions { Enabled = true, AppPath = blocked }));
        await broken.CreateAsync(DocTypeLoader.Parse("""{ "name": "Widget Two", "autoname": "W2-.####", "module": "Buying", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Sales Manager", "read": true } ] }"""), TestUsers.Admin);
        Assert.True(_engine.Registry.TryGet("Widget Two", out _));
    }
}
