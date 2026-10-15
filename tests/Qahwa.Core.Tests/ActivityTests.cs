using System.Text.Json.Nodes;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>The updates log (Version rows) and comments on every document.</summary>
public class ActivityTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private static readonly UserContext Boss = new("boss@example.com", ["Boss"]);

    public ActivityTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Deal", "isSubmittable": true,
              "fields": [
                { "name": "title", "type": "Data" }, { "name": "status", "type": "Data" },
                { "name": "margin", "type": "Currency", "permLevel": 1 }, { "name": "api_key", "type": "Password" },
                { "name": "lines", "type": "Table", "options": "Line" }
              ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true },
                { "role": "Boss", "read": true, "write": true, "create": true, "submit": true, "cancel": true },
                { "role": "Boss", "read": true, "write": true, "permLevel": 1 }
              ]
            }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "trackChanges": false, "fields": [ { "name": "text", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task<string> DealAsync(params (string, object?)[] values) =>
        (string)(await _engine.Documents.InsertAsync("Deal", Values(values), Clerk))["name"]!;

    private async Task<IReadOnlyList<Dictionary<string, object?>>> VersionsAsync(string name) =>
        (await _engine.Repository.ListAsync(_engine.Registry.Get("Version"),
            new ListQuery { Filters = [new QueryFilter("ref_docname", FilterOp.Eq, name)], OrderBy = "creation", Descending = false, Limit = 100 }));

    private async Task<IReadOnlyList<ActivityItem>> TimelineAsync(string name, IUserContext? user = null) =>
        await _engine.Activity.TimelineAsync("Deal", name, user ?? Clerk);

    private static string[][] Changed(ActivityItem item) =>
        item.Data!["changed"]!.AsArray().Select(e => e!.AsArray().Select(x => x?.ToString() ?? "").ToArray()).ToArray();

    // ---- the updates log ----

    [Fact]
    public async Task Every_document_gets_an_insert_entry_and_every_real_change_a_field_level_entry()
    {
        var id = await DealAsync(("title", "A"), ("status", "Open"));
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "Won"), ("title", "A")), Clerk); // title did not change

        var timeline = await TimelineAsync(id);
        Assert.Equal(["version", "version"], timeline.Select(i => i.Kind));
        Assert.Equal(["Update", "Insert"], timeline.Select(i => i.Event)); // newest first
        Assert.Equal([["status", "Open", "Won"]], Changed(timeline[0]));
        Assert.Equal(Clerk.User, timeline[0].User);
    }

    [Fact]
    public async Task An_update_that_changes_nothing_leaves_no_entry()
    {
        var id = await DealAsync(("title", "A"));

        await _engine.Documents.UpdateAsync("Deal", id, Values(("title", "A")), Clerk);

        Assert.Single(await VersionsAsync(id)); // only the insert
    }

    [Fact]
    public async Task Submit_and_cancel_are_logged_with_the_status_change()
    {
        var id = await DealAsync(("title", "A"));
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.SubmitAsync("Deal", id, Boss);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.CancelAsync("Deal", id, Boss);

        var timeline = await TimelineAsync(id, Boss);

        Assert.Equal(["Cancel", "Submit", "Insert"], timeline.Select(i => i.Event));
        Assert.Contains(Changed(timeline.Single(i => i.Event == "Submit")), c => c[0] == "docstatus" && c[1] == "0" && c[2] == "1");
        Assert.Contains(Changed(timeline.Single(i => i.Event == "Cancel")), c => c[0] == "docstatus" && c[2] == "2");
    }

    [Fact]
    public async Task Table_rows_added_changed_and_removed_are_logged()
    {
        var id = await DealAsync(("lines", new[] { Values(("item", "pen"), ("qty", 1)), Values(("item", "ink"), ("qty", 2)) }));
        var rows = ((IEnumerable<Dictionary<string, object?>>)(await _engine.Documents.GetAsync("Deal", id, Clerk))["lines"]!).ToList();
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await _engine.Documents.UpdateAsync("Deal", id, Values(("lines", new[]
        {
            Values(("name", rows[0]["name"]), ("item", "pen"), ("qty", 5)),   // changed
            Values(("item", "pad"), ("qty", 9)),                              // added; the "ink" row is removed
        })), Clerk);

        var lines = (await TimelineAsync(id))[0].Data!["tables"]!["lines"]!;
        Assert.Equal("pad", lines["added"]![0]!["item"]!.ToString());
        Assert.Equal("ink", lines["removed"]![0]!["item"]!.ToString());
        Assert.Equal("qty", lines["changed"]![0]!["changed"]![0]![0]!.ToString());
        Assert.Equal("1", lines["changed"]![0]!["changed"]![0]![1]!.ToString());
        Assert.Equal("5", lines["changed"]![0]!["changed"]![0]![2]!.ToString());
    }

    [Fact]
    public void Password_values_never_reach_the_log()
    {
        // Passwords are normally kept outside the DocType's table; this guards the recorder itself.
        var meta = _engine.Registry.Get("Deal");
        var existing = new Dictionary<string, object?> { ["api_key"] = "old-secret" };
        var changes = new Dictionary<string, object?> { ["api_key"] = "s3cret-value", ["title"] = "T" };

        var data = _engine.History.UpdateData(meta, existing, changes, [], new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>());

        var json = System.Text.Json.JsonSerializer.Serialize(data);
        Assert.DoesNotContain("s3cret", json);
        Assert.DoesNotContain("old-secret", json);
        Assert.Contains("api_key", json);
    }

    [Fact]
    public async Task Fields_the_reader_may_not_see_are_left_out_of_the_log()
    {
        var id = await DealAsync(("title", "A"));
        await _engine.Documents.UpdateAsync("Deal", id, Values(("margin", 40)), Boss); // level 1: only the Boss may see it
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Deal", id, Values(("margin", 50), ("title", "B")), Boss);

        var forClerk = await TimelineAsync(id, Clerk);
        var forBoss = await TimelineAsync(id, Boss);

        Assert.DoesNotContain(forClerk, i => i.Data is not null && i.Data.ToJsonString().Contains("margin"));
        Assert.Equal([["title", "A", "B"]], Changed(forClerk.First(i => i.Event == "Update")));
        Assert.Contains(forBoss, i => i.Data is not null && i.Data.ToJsonString().Contains("margin"));
        // The update that only touched margin is invisible to the clerk, not an empty row.
        Assert.Equal(2, forClerk.Count(i => i.Event is "Update" or "Insert") );
    }

    [Fact]
    public async Task A_stale_update_is_not_logged_because_the_change_did_not_happen()
    {
        var id = await DealAsync(("title", "A"));
        var seen = (DateTime)(await _engine.Documents.GetAsync("Deal", id, Clerk))["modified"]!;
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Deal", id, Values(("title", "B")), Clerk);
        var before = (await VersionsAsync(id)).Count;

        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.UpdateAsync("Deal", id, Values(("title", "C"), ("modified", seen)), Clerk));

        Assert.Equal(before, (await VersionsAsync(id)).Count);
    }

    [Fact]
    public async Task Deleting_keeps_the_log_with_a_snapshot_and_removes_the_comments()
    {
        var id = await DealAsync(("title", "A"), ("lines", new[] { Values(("item", "pen"), ("qty", 1)) }));
        await _engine.Activity.AddCommentAsync("Deal", id, "hello", Clerk);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await _engine.Documents.DeleteAsync("Deal", id, Clerk);

        var versions = await VersionsAsync(id);
        Assert.Equal("Delete", versions.Last()["event"]);
        var snapshot = JsonNode.Parse((string)versions.Last()["data"]!)!["doc"]!;
        Assert.Equal("A", snapshot["title"]!.ToString());
        Assert.Equal("pen", snapshot["lines"]![0]!["item"]!.ToString());
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Comment"), new ListQuery { Filters = [new QueryFilter("reference_name", FilterOp.Eq, id)] }));
    }

    [Fact]
    public async Task Renaming_moves_the_history_and_comments_with_the_document()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Folder", "autoname": "field:label", "allowRename": true, "fields": [ { "name": "label", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        await _engine.Documents.InsertAsync("Folder", Values(("label", "Old")), Clerk);
        await _engine.Activity.AddCommentAsync("Folder", "Old", "note", Clerk);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await _engine.Documents.RenameAsync("Folder", "Old", "New", false, Clerk);

        var timeline = await _engine.Activity.TimelineAsync("Folder", "New", Clerk);
        Assert.Equal(["Rename", "Insert"], timeline.Where(i => i.Kind == "version").Select(i => i.Event));
        Assert.Single(timeline, i => i.Kind == "comment");
    }

    [Fact]
    public async Task A_doctype_can_opt_out_and_child_tables_are_never_logged_on_their_own()
    {
        var memo = (string)(await _engine.Documents.InsertAsync("Memo", Values(("text", "x")), Clerk))["name"]!;
        await _engine.Documents.UpdateAsync("Memo", memo, Values(("text", "y")), Clerk);
        var id = await DealAsync(("lines", new[] { Values(("item", "pen"), ("qty", 1)) }));

        Assert.Empty(await VersionsAsync(memo));
        Assert.DoesNotContain((await _engine.Repository.ListAsync(_engine.Registry.Get("Version"), new ListQuery { Limit = 100 })), v => (string)v["ref_doctype"]! == "Line");
        Assert.Single(await VersionsAsync(id));
    }

    // ---- who may read it ----

    [Fact]
    public async Task The_timeline_and_comments_need_read_access_to_the_document()
    {
        var id = await DealAsync(("title", "A"));

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.TimelineAsync("Deal", id, TestUsers.Nobody));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.AddCommentAsync("Deal", id, "hi", TestUsers.Nobody));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Activity.TimelineAsync("Deal", "MISSING", Clerk));
    }

    [Fact]
    public async Task Nobody_can_rewrite_history_through_the_generic_api()
    {
        var id = await DealAsync(("title", "A"));
        var version = (string)(await VersionsAsync(id))[0]["name"]!;

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Version", version, Values(("event", "Nothing")), TestUsers.SystemManager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.DeleteAsync("Version", version, TestUsers.SystemManager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Version", Values(("ref_doctype", "Deal"), ("ref_docname", id), ("event", "Fake")), TestUsers.SystemManager));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Comment", Values(("reference_doctype", "Deal"), ("reference_name", id), ("content", "x")), TestUsers.SystemManager));
    }

    [Fact]
    public async Task Pages_walk_back_in_time()
    {
        var id = await DealAsync(("title", "0"));
        for (var i = 1; i <= 4; i++)
        {
            _engine.Clock.Advance(TimeSpan.FromMinutes(1));
            await _engine.Documents.UpdateAsync("Deal", id, Values(("title", i.ToString())), Clerk);
        }

        var first = await _engine.Activity.TimelineAsync("Deal", id, Clerk, limit: 2);
        var second = await _engine.Activity.TimelineAsync("Deal", id, Clerk, limit: 2, before: first[^1].At);

        Assert.Equal(2, first.Count);
        Assert.Equal(2, second.Count);
        Assert.True(second[0].At < first[^1].At);
        Assert.Equal(["4", "3"], first.Select(i => Changed(i)[0][2]));
        Assert.Equal(["2", "1"], second.Select(i => Changed(i)[0][2]));
    }

    // ---- comments ----

    [Fact]
    public async Task People_comment_and_their_names_are_shown()
    {
        await _engine.Documents.InsertAsync("User", Values(("email", Clerk.User), ("full_name", "Carla Clerk")), TestUsers.Admin);
        var id = await DealAsync(("title", "A"));

        var comment = await _engine.Activity.AddCommentAsync("Deal", id, "  Looks good @boss@example.com  ", Clerk);

        Assert.Equal("Looks good @boss@example.com", comment.Content);
        Assert.Equal("Carla Clerk", comment.UserName);
        Assert.True(comment.CanEdit);
        Assert.Equal("Looks good @boss@example.com", (await TimelineAsync(id)).Single(i => i.Kind == "comment").Content);
        Assert.False((await TimelineAsync(id, Boss)).Single(i => i.Kind == "comment").CanEdit);
    }

    [Fact]
    public async Task Only_the_author_edits_a_comment_and_an_administrator_may_delete_it()
    {
        var id = await DealAsync(("title", "A"));
        var comment = await _engine.Activity.AddCommentAsync("Deal", id, "mine", Clerk);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.UpdateCommentAsync("Deal", id, comment.Id, "hijack", Boss));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.DeleteCommentAsync("Deal", id, comment.Id, Boss));
        var edited = await _engine.Activity.UpdateCommentAsync("Deal", id, comment.Id, "better", Clerk);
        Assert.True(edited.Edited);
        Assert.Equal("better", (await TimelineAsync(id)).Single(i => i.Kind == "comment").Content);

        await _engine.Activity.DeleteCommentAsync("Deal", id, comment.Id, TestUsers.Admin);
        Assert.DoesNotContain(await TimelineAsync(id), i => i.Kind == "comment");
    }

    [Fact]
    public async Task A_comment_id_only_works_on_its_own_document()
    {
        var a = await DealAsync(("title", "A"));
        var b = await DealAsync(("title", "B"));
        var comment = await _engine.Activity.AddCommentAsync("Deal", a, "on A", Clerk);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Activity.UpdateCommentAsync("Deal", b, comment.Id, "x", Clerk));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Activity.DeleteCommentAsync("Deal", b, comment.Id, Clerk));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Activity.UpdateCommentAsync("Deal", a, "nope", "x", Clerk));
    }

    [Fact]
    public async Task Comments_are_validated()
    {
        var id = await DealAsync(("title", "A"));

        foreach (var bad in new[] { null, "", "   ", new string('x', HistoryRecorder.MaxContentLength + 1), "bell\a" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Activity.AddCommentAsync("Deal", id, bad, Clerk));
        await _engine.Activity.AddCommentAsync("Deal", id, "line one\nline two\ttabbed", Clerk);
    }

    [Fact]
    public async Task A_submitted_or_cancelled_document_can_still_be_commented()
    {
        var id = await DealAsync(("title", "A"));
        await _engine.Documents.SubmitAsync("Deal", id, Boss);
        await _engine.Activity.AddCommentAsync("Deal", id, "after submit", Clerk);
        await _engine.Documents.CancelAsync("Deal", id, Boss);

        await _engine.Activity.AddCommentAsync("Deal", id, "after cancel", Clerk);

        Assert.Equal(2, (await TimelineAsync(id)).Count(i => i.Kind == "comment"));
    }

    [Fact]
    public async Task Sharing_leaves_a_system_note_nobody_can_edit()
    {
        await _engine.Documents.InsertAsync("User", Values(("email", "pal@example.com"), ("full_name", "Pal")), TestUsers.Admin);
        var id = await DealAsync(("title", "A"));

        await _engine.ShareManager.SetAsync("Deal", id, "pal@example.com", true, false, Clerk);
        await _engine.ShareManager.SetAsync("Deal", id, "pal@example.com", true, true, Clerk);
        await _engine.ShareManager.SetAsync("Deal", id, "pal@example.com", false, false, Clerk);

        var notes = (await TimelineAsync(id)).Where(i => i.Kind == "comment").Select(i => i.Content).ToList();
        Assert.Contains("Shared with pal@example.com (can read)", notes);
        Assert.Contains("Sharing with pal@example.com changed (can edit)", notes);
        Assert.Contains("Stopped sharing with pal@example.com", notes);
        var note = (await TimelineAsync(id)).First(i => i.Kind == "comment");
        Assert.Equal("Info", note.Event);
        Assert.False(note.CanEdit);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.UpdateCommentAsync("Deal", id, note.Id, "x", Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.DeleteCommentAsync("Deal", id, note.Id, TestUsers.Admin));
    }
}
