using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DeletedDocumentTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Boss = new("boss@example.com", ["System Manager", "Clerk"]);
    private readonly DeletedDocumentService _deleted;

    public DeletedDocumentTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo Line", "isChildTable": true, "fields": [ { "name": "text", "type": "Data" }, { "name": "qty", "type": "Int" } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "autoname": "field:title",
              "fields": [ { "name": "title", "type": "Data", "required": true }, { "name": "amount", "type": "Decimal" }, { "name": "due", "type": "Date" },
                          { "name": "lines", "type": "Table", "options": "Memo Line" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        _deleted = new DeletedDocumentService(_engine.Registry, _engine.Repository, _engine.Permissions, _engine.History, _engine.Clock);
    }

    private async Task<Dictionary<string, object?>> KeptAsync() =>
        (await _engine.Repository.ListAsync(_engine.Registry.Get("Deleted Document"), new ListQuery { Limit = 10 })).Single();

    private async Task MemoAsync(string title) =>
        await _engine.Documents.InsertAsync("Memo", Values(("title", title), ("amount", 12.5m), ("due", "2026-03-04"),
            ("lines", new object[] { Values(("text", "one"), ("qty", 1)), Values(("text", "two"), ("qty", 2)) })), Ann);

    [Fact]
    public async Task A_deleted_document_is_kept_and_can_be_restored_with_its_rows()
    {
        await MemoAsync("m1");
        await _engine.Documents.DeleteAsync("Memo", "m1", Ann);
        var kept = await KeptAsync();
        Assert.Equal("Memo", kept["deleted_doctype"]);
        Assert.Equal("m1", kept["deleted_name"]);

        var name = await _deleted.RestoreAsync((string)kept["name"]!, TestUsers.Admin);

        Assert.Equal("m1", name);
        var back = await _engine.Documents.GetAsync("Memo", "m1", Ann);
        Assert.Equal(12.5m, back["amount"]);
        Assert.Equal(new DateOnly(2026, 3, 4), back["due"]);
        var lines = ((IEnumerable<Dictionary<string, object?>>)back["lines"]!).ToList();
        Assert.Equal(["one", "two"], lines.Select(l => (string)l["text"]!));
        Assert.Empty((await _engine.Repository.ListAsync(_engine.Registry.Get("Deleted Document"), new ListQuery { Limit = 10 })));
    }

    [Fact]
    public async Task Restoring_over_a_new_document_with_the_same_name_is_refused_and_keeps_the_copy()
    {
        await MemoAsync("m1");
        await _engine.Documents.DeleteAsync("Memo", "m1", Ann);
        var id = (string)(await KeptAsync())["name"]!;
        await MemoAsync("m1");

        await Assert.ThrowsAsync<ConflictException>(() => _deleted.RestoreAsync(id, TestUsers.Admin));

        Assert.NotNull(await KeptAsync());
    }

    [Fact]
    public async Task Only_a_manager_who_may_create_the_document_can_restore_it()
    {
        await MemoAsync("m1");
        await _engine.Documents.DeleteAsync("Memo", "m1", Ann);
        var id = (string)(await KeptAsync())["name"]!;

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _deleted.RestoreAsync(id, Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _deleted.RestoreAsync(id, UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _deleted.RestoreAsync("DEL-NOPE", Boss));
        Assert.Equal("m1", await _deleted.RestoreAsync(id, Boss));
    }

    [Fact]
    public async Task Framework_documents_and_child_rows_leave_no_copy_and_a_failed_delete_leaves_none_either()
    {
        await _engine.Documents.InsertAsync("Memo", Values(("title", "keep")), Ann);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.DeleteAsync("Memo", "ghost", Ann));

        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Deleted Document"), new ListQuery { Limit = 10 }));
        Assert.False(DeletedDocumentService.Keeps(_engine.Registry.Get("Memo Line")));
        Assert.False(DeletedDocumentService.Keeps(_engine.Registry.Get("Deleted Document")));
    }

    [Fact]
    public async Task Old_copies_are_removed_after_the_retention_period_and_recent_ones_stay()
    {
        await MemoAsync("old");
        await _engine.Documents.DeleteAsync("Memo", "old", Ann);
        ((TestClock)_engine.Clock).Advance(TimeSpan.FromDays(40));
        await MemoAsync("new");
        await _engine.Documents.DeleteAsync("Memo", "new", Ann);

        var removed = await _deleted.PurgeAsync(TimeSpan.FromDays(30));

        Assert.Equal(1, removed);
        Assert.Equal("new", (await KeptAsync())["deleted_name"]);
    }

    [Fact]
    public async Task A_damaged_copy_is_reported_not_crashed_on()
    {
        await MemoAsync("m1");
        await _engine.Documents.DeleteAsync("Memo", "m1", Ann);
        var kept = await KeptAsync();
        await _engine.Repository.UpdateAsync(_engine.Registry.Get("Deleted Document"), (string)kept["name"]!, new Dictionary<string, object?> { ["data"] = "[1,2" }, null);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _deleted.RestoreAsync((string)kept["name"]!, TestUsers.Admin));
    }
}
