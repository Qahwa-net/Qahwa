using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class LabelTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);
    private static readonly UserContext Own = new("own@example.com", ["Own Only"]);

    public LabelTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "autoname": "field:title", "fields": [ { "name": "title", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true }, { "role": "Viewer", "read": true },
                               { "role": "Own Only", "read": true, "write": true, "create": true, "ifOwner": true } ] }
            """));
    }

    private Task TicketAsync(string title, UserContext? by = null) => _engine.Documents.InsertAsync("Ticket", Values(("title", title)), by ?? Ann);

    private async Task<string[]> NamesAsync(UserContext user, params RawFilter[] filters) =>
        (await _engine.Documents.ListAsync("Ticket", new ListRequest { Fields = ["name"], OrderBy = "name", Descending = false, Filters = filters }, user)).Data.Select(r => (string)r["name"]!).ToArray();

    [Fact]
    public async Task Tags_are_added_once_normalised_and_removed()
    {
        await TicketAsync("t1");

        await _engine.Labels.AddTagAsync("Ticket", "t1", "  Urgent ", Ann);
        var tags = await _engine.Labels.AddTagAsync("Ticket", "t1", "urgent", Ann);
        tags = await _engine.Labels.AddTagAsync("Ticket", "t1", "Billing", Ann);

        Assert.Equal(["billing", "urgent"], tags);
        Assert.Equal(["billing"], await _engine.Labels.RemoveTagAsync("Ticket", "t1", "URGENT", Ann));
        Assert.Equal(["billing"], await _engine.Labels.TagsAsync("Ticket", "t1", Vic));
    }

    [Fact]
    public async Task Bad_tags_are_refused_and_there_is_a_limit_per_document()
    {
        await TicketAsync("t1");

        foreach (var bad in new[] { "", "   ", "a,b", new string('x', LabelService.MaxTagLength + 1), "tab\there" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Labels.AddTagAsync("Ticket", "t1", bad, Ann));
        for (var i = 0; i < LabelService.MaxTagsPerDocument; i++)
            await _engine.Labels.AddTagAsync("Ticket", "t1", $"tag{i}", Ann);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Labels.AddTagAsync("Ticket", "t1", "one-too-many", Ann));
    }

    [Fact]
    public async Task Tagging_needs_the_right_to_change_the_document_and_reading_it_to_see_them()
    {
        await TicketAsync("t1");
        await TicketAsync("mine", Own);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Labels.AddTagAsync("Ticket", "t1", "x", Vic));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Labels.TagsAsync("Ticket", "ghost", Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Labels.TagsAsync("Ticket", "t1", Own)); // not theirs: looks like it is not there
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Labels.AddTagAsync("User", "ann@example.com", "x", Ann));
    }

    [Fact]
    public async Task Lists_can_be_filtered_by_tag_and_by_who_liked()
    {
        await TicketAsync("a");
        await TicketAsync("b");
        await TicketAsync("c");
        await _engine.Labels.AddTagAsync("Ticket", "a", "urgent", Ann);
        await _engine.Labels.AddTagAsync("Ticket", "c", "urgent", Ann);
        await _engine.Labels.SetLikeAsync("Ticket", "b", true, Bob);
        await _engine.Labels.SetLikeAsync("Ticket", "c", true, Bob);
        await _engine.Labels.SetLikeAsync("Ticket", "c", true, Ann);

        Assert.Equal(["a", "c"], await NamesAsync(Ann, new RawFilter("_tag", "=", "URGENT")));
        Assert.Equal(["b", "c"], await NamesAsync(Ann, new RawFilter("_liked_by", "=", "bob@example.com")));
        Assert.Equal(["c"], await NamesAsync(Bob, new RawFilter("_liked_by", "=", "__user"), new RawFilter("_tag", "=", "urgent")));
        Assert.Empty(await NamesAsync(Ann, new RawFilter("_tag", "=", "nothing")));
        var total = (await _engine.Documents.ListAsync("Ticket", new ListRequest { Filters = [new RawFilter("_tag", "=", "urgent")], Limit = 1 }, Ann)).Total;
        Assert.Equal(2, total);
        await Assert.ThrowsAsync<DocumentValidationException>(() => NamesAsync(Ann, new RawFilter("_tag", "like", "u%")));
        await Assert.ThrowsAsync<DocumentValidationException>(() => NamesAsync(Ann, new RawFilter("_tag", "=", "a,b")));
    }

    [Fact]
    public async Task A_tag_filter_cannot_reveal_documents_the_person_may_not_see()
    {
        await TicketAsync("secret");
        await TicketAsync("mine", Own);
        await _engine.Labels.AddTagAsync("Ticket", "secret", "x", Ann);
        await _engine.Labels.AddTagAsync("Ticket", "mine", "x", Own);

        Assert.Equal(["mine"], await NamesAsync(Own, new RawFilter("_tag", "=", "x")));
        Assert.Equal([new TagCount("x", 1)], await _engine.Labels.PopularTagsAsync("Ticket", Own));
    }

    [Fact]
    public async Task Likes_are_one_per_person_and_can_be_taken_back()
    {
        await TicketAsync("t1");

        await _engine.Labels.SetLikeAsync("Ticket", "t1", true, Ann);
        await _engine.Labels.SetLikeAsync("Ticket", "t1", true, Ann);
        var state = await _engine.Labels.SetLikeAsync("Ticket", "t1", true, Vic);

        Assert.Equal(new LikeState(true, 2), state);
        Assert.Equal(new LikeState(false, 2), await _engine.Labels.LikeStateAsync("Ticket", "t1", Bob));
        Assert.Equal(new LikeState(false, 1), await _engine.Labels.SetLikeAsync("Ticket", "t1", false, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Labels.SetLikeAsync("Ticket", "t1", true, Own));
    }

    [Fact]
    public async Task Popular_tags_count_documents_most_used_first()
    {
        await TicketAsync("a");
        await TicketAsync("b");
        await _engine.Labels.AddTagAsync("Ticket", "a", "x", Ann);
        await _engine.Labels.AddTagAsync("Ticket", "b", "x", Ann);
        await _engine.Labels.AddTagAsync("Ticket", "b", "y", Ann);

        Assert.Equal([new TagCount("x", 2), new TagCount("y", 1)], await _engine.Labels.PopularTagsAsync("Ticket", Vic));
    }

    [Fact]
    public async Task Tags_and_likes_go_with_a_deleted_document_and_follow_a_rename()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Folder", "autoname": "field:label", "allowRename": true, "fields": [ { "name": "label", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
        await _engine.Documents.InsertAsync("Folder", Values(("label", "f1")), Ann);
        await _engine.Labels.AddTagAsync("Folder", "f1", "keep", Ann);
        await _engine.Labels.SetLikeAsync("Folder", "f1", true, Ann);

        await _engine.Documents.RenameAsync("Folder", "f1", "f2", false, Ann);
        Assert.Equal(["keep"], await _engine.Labels.TagsAsync("Folder", "f2", Ann));
        Assert.True((await _engine.Labels.LikeStateAsync("Folder", "f2", Ann)).Liked);

        await _engine.Documents.DeleteAsync("Folder", "f2", Ann);
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Document Tag"), new ListQuery { Limit = 10 }));
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Document Like"), new ListQuery { Limit = 10 }));
    }
}
