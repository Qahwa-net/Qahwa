using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Single and tree DocTypes.</summary>
public class SpecialDocTypeTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Staff = new("staff@example.com", ["Staff"]);

    public SpecialDocTypeTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "App Settings", "isSingle": true,
              "fields": [ { "name": "company_name", "type": "Data", "default": "Acme" }, { "name": "limit", "type": "Int" } ],
              "permissions": [ { "role": "Staff", "read": true, "write": true, "create": true, "delete": true } ]
            }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Region", "isTree": true, "autoname": "field:region_name",
              "fields": [ { "name": "region_name", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Staff", "read": true, "write": true, "create": true, "delete": true } ]
            }
            """));
    }

    // ---- single ----

    [Fact]
    public async Task A_single_doctype_shows_its_defaults_until_it_is_first_saved()
    {
        var doc = await _engine.Documents.GetAsync("App Settings", "App Settings", Staff);

        Assert.Equal("Acme", doc["company_name"]);
        Assert.Equal("App Settings", doc["name"]);
        Assert.Empty((await _engine.Documents.ListAsync("App Settings", new ListRequest(), Staff)).Data);
    }

    [Fact]
    public async Task Saving_a_single_creates_its_one_document_and_later_saves_update_it()
    {
        var first = await _engine.Documents.UpdateAsync("App Settings", "App Settings", Values(("limit", 5)), Staff);
        var second = await _engine.Documents.UpdateAsync("App Settings", "App Settings", Values(("limit", 9)), Staff);

        Assert.Equal("App Settings", first["name"]);
        Assert.Equal(9, second["limit"]);
        Assert.Equal(1, (await _engine.Documents.ListAsync("App Settings", new ListRequest(), Staff)).Total);
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.InsertAsync("App Settings", Values(("limit", 1)), Staff));
    }

    [Fact]
    public async Task A_single_cannot_be_deleted_and_only_other_names_are_not_found()
    {
        await _engine.Documents.UpdateAsync("App Settings", "App Settings", Values(("limit", 5)), Staff);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.DeleteAsync("App Settings", "App Settings", TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("App Settings", "Other", Staff));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.UpdateAsync("App Settings", "Other", Values(("limit", 1)), Staff));
    }

    [Fact]
    public async Task Reading_a_single_still_needs_permission()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("App Settings", "App Settings", TestUsers.Nobody));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("App Settings", "App Settings", Values(("limit", 1)), TestUsers.Nobody));
    }

    [Fact]
    public void Single_definitions_are_checked()
    {
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "S", "isSingle": true, "autoname": "S-.####", "fields": [ { "name": "a", "type": "Data" } ] }"""));
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "S", "isSingle": true, "isTree": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "S", "isSingle": true, "allowRename": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "S", "isChildTable": true, "isSingle": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
    }

    // ---- tree ----

    private async Task NodeAsync(string name, string? parent = null, bool group = false) =>
        await _engine.Documents.InsertAsync("Region", Values(("region_name", name), ("parent_node", parent), ("is_group", group)), Staff);

    [Fact]
    public void A_tree_gets_its_parent_and_group_fields()
    {
        var meta = _engine.Registry.Get("Region");

        Assert.Contains(meta.Fields, f => f.Name == "parent_node" && f.Type == Qahwa.Abstractions.FieldType.Link && f.Options == "Region");
        Assert.Contains(meta.Fields, f => f.Name == "is_group" && f.Type == Qahwa.Abstractions.FieldType.Check);
    }

    [Fact]
    public async Task Only_groups_can_have_children()
    {
        await NodeAsync("World", group: true);
        await NodeAsync("Atlantis");

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => NodeAsync("Deep", "Atlantis"));
        Assert.Contains(ex.Errors, e => e.Field == "parent_node");
        await NodeAsync("Europe", "World");
    }

    [Fact]
    public async Task A_node_cannot_become_its_own_ancestor()
    {
        await NodeAsync("World", group: true);
        await NodeAsync("Europe", "World", group: true);
        await NodeAsync("France", "Europe", group: true);

        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Region", "World", Values(("parent_node", "France")), Staff));
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Region", "Europe", Values(("parent_node", "Europe")), Staff));
        await _engine.Documents.UpdateAsync("Region", "France", Values(("parent_node", "World")), Staff); // moving up is fine
    }

    [Fact]
    public async Task A_node_with_children_cannot_be_deleted_or_stop_being_a_group()
    {
        await NodeAsync("World", group: true);
        await NodeAsync("Europe", "World");

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.DeleteAsync("Region", "World", Staff));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Region", "World", Values(("is_group", false)), Staff));

        await _engine.Documents.DeleteAsync("Region", "Europe", Staff);
        await _engine.Documents.DeleteAsync("Region", "World", Staff);
    }

    [Fact]
    public async Task The_tree_lists_roots_and_the_children_of_a_node()
    {
        await NodeAsync("World", group: true);
        await NodeAsync("Europe", "World", group: true);
        await NodeAsync("Asia", "World");
        await NodeAsync("Mars", group: true);

        var roots = (await _engine.Documents.TreeChildrenAsync("Region", null, Staff)).Select(r => r["name"]).ToList();
        var kids = (await _engine.Documents.TreeChildrenAsync("Region", "World", Staff)).ToList();

        Assert.Equal(["Mars", "World"], roots);
        Assert.Equal(["Asia", "Europe"], kids.Select(r => r["name"]));
        Assert.True((bool)kids.Single(r => r["name"]!.Equals("Europe"))["is_group"]!);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.TreeChildrenAsync("App Settings", null, Staff));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.TreeChildrenAsync("Region", null, TestUsers.Nobody));
    }
}

public class SingleDesignerTests
{
    private readonly TestEngine _engine = new();

    [Fact]
    public async Task A_single_doctype_can_be_designed_used_and_deleted_even_though_its_document_cannot()
    {
        var definition = DocTypeLoader.Parse("""
            { "name": "Mail Settings", "isSingle": true, "fields": [ { "name": "host", "type": "Data" } ],
              "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true } ] }
            """);
        await _engine.Designer.CreateAsync(definition, TestUsers.Admin);
        await _engine.Documents.UpdateAsync("Mail Settings", "Mail Settings", Values(("host", "smtp")), TestUsers.SystemManager);

        await _engine.Designer.DeleteAsync("Mail Settings", TestUsers.Admin);

        Assert.False(_engine.Registry.TryGet("Mail Settings", out _));
    }

    [Fact]
    public async Task Single_and_tree_cannot_be_switched_after_creation_and_trees_get_their_fields_from_the_designer()
    {
        var tree = DocTypeLoader.Parse("""{ "name": "Dept", "isTree": true, "fields": [ { "name": "title", "type": "Data" } ] }""");
        await _engine.Designer.CreateAsync(tree, TestUsers.Admin);
        Assert.Contains(_engine.Registry.Get("Dept").Fields, f => f.Name == "parent_node");

        var flipped = _engine.Registry.Get("Dept") with { IsTree = false };
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Designer.UpdateAsync("Dept", flipped, null, TestUsers.Admin));
        var single = _engine.Registry.Get("Dept") with { IsSingle = true, IsTree = false };
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Designer.UpdateAsync("Dept", single, null, TestUsers.Admin));
    }
}
