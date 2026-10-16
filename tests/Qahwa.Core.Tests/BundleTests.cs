using Qahwa.Abstractions;
using System.Text.Json;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class BundleTests
{
    private const string Supplier = """{ "name": "Supplier", "fields": [ { "name": "supplier_name", "type": "Data" } ], "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true } ] }""";

    private readonly TestEngine _source = new();
    private readonly TestEngine _target = new();

    private static string Wrap(params string[] items) =>
        $$"""{ "format": "qahwa-bundle", "version": 1, "name": "t", "items": [ {{string.Join(",", items)}} ] }""";

    private static string Item(string kind, string doctype, string definition) =>
        $$"""{ "kind": "{{kind}}", "doctype": "{{doctype}}", "definition": {{definition}} }""";

    private async Task BuildSourceAsync()
    {
        await _source.Designer.CreateAsync(DocTypeLoader.Parse(Supplier), TestUsers.SystemManager);
        await _source.Customization.SaveAsync("Customer", new Customization { CustomFields = [new DocFieldMeta { Name = "custom_phone", Type = FieldType.Data, Label = "Phone" }] }, null, TestUsers.SystemManager);
        await _source.NotificationRuleManager.SaveAsync("Customer", new NotificationRuleSet { DocType = "Customer", Rules = [new NotificationRule { Name = "Changed", Event = NotificationRule.Update, Message = "Changed {{name}}", NotifyOwner = true }] }, null, TestUsers.SystemManager);
    }

    [Fact]
    public async Task What_was_built_in_one_site_can_be_moved_to_another()
    {
        await BuildSourceAsync();
        var file = BundleService.ToJson(await _source.Bundle.ExportAsync("shop", null, TestUsers.SystemManager));

        var report = await _target.Bundle.ImportAsync(BundleService.FromJson(file), dryRun: false, TestUsers.SystemManager);

        Assert.True(report.Failed == 0, string.Join("|", report.Items.Where(i => !i.Ok).Select(i => i.Kind + ":" + i.Message)));
        Assert.Equal(3, report.Applied);
        Assert.NotNull(_target.Registry.Get("Supplier"));
        Assert.Contains(_target.Registry.Get("Customer")!.Fields, f => f.Name == "custom_phone");
        Assert.True((await _target.NotificationRuleManager.GetAsync("Customer", TestUsers.SystemManager)).Exists);
    }

    [Fact]
    public async Task Importing_again_updates_instead_of_failing()
    {
        await BuildSourceAsync();
        var bundle = BundleService.FromJson(BundleService.ToJson(await _source.Bundle.ExportAsync("shop", null, TestUsers.SystemManager)));
        await _target.Bundle.ImportAsync(bundle, false, TestUsers.SystemManager);

        var again = await _target.Bundle.ImportAsync(bundle, false, TestUsers.SystemManager);

        Assert.Equal(0, again.Failed);
        Assert.All(again.Items, i => Assert.Equal("update", i.Action));
    }

    [Fact]
    public async Task A_preview_changes_nothing_and_says_what_would_happen()
    {
        await BuildSourceAsync();
        var bundle = BundleService.FromJson(BundleService.ToJson(await _source.Bundle.ExportAsync("shop", null, TestUsers.SystemManager)));

        var report = await _target.Bundle.ImportAsync(bundle, dryRun: true, TestUsers.SystemManager);

        Assert.True(report.DryRun);
        Assert.Equal(3, report.Applied);
        Assert.All(report.Items, i => Assert.Equal("create", i.Action));
        Assert.Null(_target.Registry.SourceOf("Supplier"));
        Assert.False((await _target.NotificationRuleManager.GetAsync("Customer", TestUsers.SystemManager)).Exists);
    }

    [Fact]
    public async Task Only_the_asked_kinds_are_exported_and_webhooks_are_not_a_kind()
    {
        await BuildSourceAsync();

        var file = BundleService.ToJson(await _source.Bundle.ExportAsync("shop", [BundleKind.NotificationRules], TestUsers.SystemManager));

        Assert.DoesNotContain("Supplier", file);
        Assert.DoesNotContain("hooks.example.com", file);
        Assert.Contains("Changed", file);
        Assert.False(Enum.TryParse<BundleKind>("Webhooks", out _));
        Assert.Throws<DocumentValidationException>(() => BundleService.FromJson(Wrap(Item("Webhooks", "Customer", "{}"))));
    }

    [Fact]
    public async Task One_bad_item_fails_alone_and_says_why()
    {
        var file = Wrap(
            Item("NotificationRules", "Customer", """{ "docType": "Customer", "rules": [ { "name": "Bad", "event": "Nope", "message": "x" } ] }"""),
            Item("Customization", "Customer", """{ "customFields": [ { "name": "custom_ok", "type": "Data" } ] }"""),
            Item("Workflow", "Nowhere", """{ "docType": "Nowhere" }"""));

        var report = await _target.Bundle.ImportAsync(BundleService.FromJson(file), false, TestUsers.SystemManager);

        Assert.Equal(1, report.Applied);
        Assert.Equal(2, report.Failed);
        Assert.Contains(_target.Registry.Get("Customer")!.Fields, f => f.Name == "custom_ok");
        Assert.Contains("does not exist", report.Items.Single(i => i.DocType == "Nowhere").Message);
    }

    [Fact]
    public async Task A_bundle_cannot_overwrite_a_built_in_doctype()
    {
        var file = Wrap(Item("DocType", "Customer", """{ "name": "Customer", "fields": [ { "name": "x", "type": "Data" } ] }"""));

        var report = await _target.Bundle.ImportAsync(BundleService.FromJson(file), false, TestUsers.SystemManager);

        Assert.Equal(1, report.Failed);
        Assert.Contains("built-in", report.Items[0].Message);
        Assert.DoesNotContain(_target.Registry.Get("Customer")!.Fields, f => f.Name == "x");
    }

    [Fact]
    public async Task Hostile_names_in_a_definition_are_refused()
    {
        var file = Wrap(Item("DocType", "Evil", """{ "name": "Evil", "fields": [ { "name": "a\"; drop table \"tabCustomer\"; --", "type": "Data" } ] }"""));

        var report = await _target.Bundle.ImportAsync(BundleService.FromJson(file), false, TestUsers.SystemManager);

        Assert.Equal(1, report.Failed);
        Assert.Null(_target.Registry.SourceOf("Evil"));
    }

    [Fact]
    public async Task Only_a_system_manager_can_export_or_import()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _target.Bundle.ExportAsync("x", null, new UserContext("ann@example.com", ["Clerk"])));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _target.Bundle.ImportAsync(new AppBundle(), false, new UserContext("ann@example.com", ["Clerk"])));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "format": "other", "version": 1, "items": [] }""")]
    [InlineData("""{ "format": "qahwa-bundle", "version": 99, "items": [] }""")]
    [InlineData("""{ "format": "qahwa-bundle", "version": 1 }""")]
    [InlineData("""{ "format": "qahwa-bundle", "version": 1, "items": [ { "kind": "Script", "doctype": "A", "definition": {} } ] }""")]
    [InlineData("""{ "format": "qahwa-bundle", "version": 1, "items": [ { "kind": "Workflow", "doctype": "A", "definition": "x" } ] }""")]
    [InlineData("""{ "format": "qahwa-bundle", "version": 1, "items": [ { "kind": 7, "doctype": "A", "definition": {} } ] }""")]
    public void A_file_that_is_not_a_bundle_is_refused(string text) =>
        Assert.Throws<DocumentValidationException>(() => BundleService.FromJson(text));

    [Fact]
    public void Too_many_items_are_refused()
    {
        var items = string.Join(",", Enumerable.Repeat(Item("Workflow", "A", "{}"), AppBundle.MaxItems + 1));

        Assert.Throws<DocumentValidationException>(() => BundleService.FromJson(Wrap(items)));
    }

    [Fact]
    public async Task Child_tables_are_created_before_the_doctypes_that_use_them()
    {
        var file = Wrap(
            Item("DocType", "Order2", """{ "name": "Order2", "fields": [ { "name": "lines", "type": "Table", "options": "Line2" } ], "permissions": [] }"""),
            Item("DocType", "Line2", """{ "name": "Line2", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" } ] }"""));

        var report = await _target.Bundle.ImportAsync(BundleService.FromJson(file), false, TestUsers.SystemManager);

        Assert.Equal(0, report.Failed);
        Assert.NotNull(_target.Registry.Get("Order2"));
    }

    [Fact]
    public async Task Exported_text_is_valid_json_with_the_header()
    {
        await BuildSourceAsync();

        using var json = JsonDocument.Parse(BundleService.ToJson(await _source.Bundle.ExportAsync("shop", null, TestUsers.SystemManager)));

        Assert.Equal("qahwa-bundle", json.RootElement.GetProperty("format").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal("shop", json.RootElement.GetProperty("name").GetString());
    }
}
