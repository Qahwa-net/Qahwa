using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class BulkRenameTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);
    private readonly BulkService _bulk;

    public BulkRenameTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Folder", "autoname": "field:label", "allowRename": true, "fields": [ { "name": "label", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true }, { "role": "Viewer", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Fixed", "autoname": "field:label", "fields": [ { "name": "label", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Shelf", "autoname": "hash", "fields": [ { "name": "folder", "type": "Link", "options": "Folder" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        _bulk = new BulkService(_engine.Registry, _engine.Documents);
    }

    private Task FolderAsync(string label) => _engine.Documents.InsertAsync("Folder", Values(("label", label)), Ann);

    private async Task<string[]> NamesAsync() =>
        (await _engine.Documents.ListAsync("Folder", new ListRequest { Fields = ["name"], OrderBy = "name", Descending = false, Limit = 100 }, Ann)).Data.Select(r => (string)r["name"]!).ToArray();

    [Fact]
    public async Task Several_documents_are_renamed_at_once_and_links_to_them_follow()
    {
        await FolderAsync("old-1");
        await FolderAsync("old-2");
        await _engine.Documents.InsertAsync("Shelf", Values(("folder", "old-1")), Ann);

        var results = await _bulk.RenameAsync("Folder", [new("old-1", "new-1"), new("old-2", "new-2")], merge: false, Ann);

        Assert.All(results, r => Assert.True(r.Ok));
        Assert.Equal(["new-1", "new-2"], await NamesAsync());
        var shelf = (await _engine.Documents.ListAsync("Shelf", new ListRequest { Fields = ["folder"] }, Ann)).Data.Single();
        Assert.Equal("new-1", shelf["folder"]);
    }

    [Fact]
    public async Task A_name_that_is_taken_fails_for_that_document_only_unless_merging_is_asked_for()
    {
        await FolderAsync("a");
        await FolderAsync("b");
        await FolderAsync("c");

        var results = await _bulk.RenameAsync("Folder", [new("a", "b"), new("c", "z")], merge: false, Ann);

        Assert.False(results[0].Ok);
        Assert.True(results[1].Ok);
        Assert.Equal(["a", "b", "z"], await NamesAsync());

        var merged = await _bulk.RenameAsync("Folder", [new("a", "b")], merge: true, Ann);
        Assert.True(merged[0].Ok, merged[0].Error);
        Assert.Equal(["b", "z"], await NamesAsync());
    }

    [Fact]
    public async Task Odd_requests_are_refused_before_anything_changes()
    {
        await FolderAsync("a");
        await FolderAsync("b");

        await Assert.ThrowsAsync<DocumentValidationException>(() => _bulk.RenameAsync("Folder", [new("a", "x"), new("b", "x")], false, Ann)); // the same new name twice
        await Assert.ThrowsAsync<DocumentValidationException>(() => _bulk.RenameAsync("Folder", [new("a", "x"), new("a", "y")], false, Ann)); // a document twice
        await Assert.ThrowsAsync<DocumentValidationException>(() => _bulk.RenameAsync("Folder", [], false, Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _bulk.RenameAsync("Folder", null, false, Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _bulk.RenameAsync("Folder", Enumerable.Range(0, 101).Select(i => new RenamePair($"n{i}", $"m{i}")).ToList(), false, Ann));
        Assert.Equal(["a", "b"], await NamesAsync());
    }

    [Fact]
    public async Task Empty_same_and_missing_names_are_reported_per_document()
    {
        await FolderAsync("a");
        await FolderAsync("b");

        var results = await _bulk.RenameAsync("Folder", [new("a", ""), new("b", "b"), new("ghost", "g")], false, Ann);

        Assert.All(results, r => Assert.False(r.Ok));
        Assert.Equal(["a", "b"], await NamesAsync());
    }

    [Fact]
    public async Task The_right_to_write_and_a_DocType_that_allows_renaming_are_needed()
    {
        await FolderAsync("a");
        await _engine.Documents.InsertAsync("Fixed", Values(("label", "f")), Ann);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _bulk.RenameAsync("Folder", [new("a", "b")], false, Vic));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _bulk.RenameAsync("Folder", [new("a", "b")], false, new UserContext("Guest", [])));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _bulk.RenameAsync("Fixed", [new("f", "g")], false, Ann));
        Assert.Equal(["a"], await NamesAsync());
    }

    [Fact]
    public async Task Hostile_names_are_data_or_refused_as_in_a_single_rename()
    {
        await FolderAsync("a");

        var results = await _bulk.RenameAsync("Folder", [new("a", "x'); DROP TABLE tabFolder; --")], false, Ann);

        Assert.True(results[0].Ok); // any text may be a name; it is only ever a parameter
        Assert.Equal(["x'); DROP TABLE tabFolder; --"], await NamesAsync());
        var tooLong = await _bulk.RenameAsync("Folder", [new("x'); DROP TABLE tabFolder; --", new string('n', 200))], false, Ann);
        Assert.False(tooLong[0].Ok);
    }
}
