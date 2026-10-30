using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Assigning, following and notifications.</summary>
public class CollaborationTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]); // cannot read Deal

    public CollaborationTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Deal", "fields": [ { "name": "title", "type": "Data" }, { "name": "status", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
    }

    private async Task UserAsync(UserContext person, string fullName, bool enabled = true)
    {
        foreach (var role in person.Roles)
        {
            if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), role))
                await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
        }

        await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", fullName), ("enabled", enabled)), TestUsers.Admin);
        foreach (var role in person.Roles)
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
    }

    private async Task AllUsersAsync()
    {
        await UserAsync(Ann, "Ann Lee");
        await UserAsync(Bob, "Bob Stone");
        await UserAsync(Eve, "Eve Outsider");
    }

    private async Task<string> DealAsync(UserContext by, string title = "Big deal") =>
        (string)(await _engine.Documents.InsertAsync("Deal", Values(("title", title)), by))["name"]!;

    private async Task<IReadOnlyList<NotificationItem>> InboxAsync(UserContext person) =>
        await _engine.Notifications.ListAsync(person, unreadOnly: false, limit: 50, before: null);

    // ---- following ----

    [Fact]
    public async Task Whoever_creates_a_document_follows_it_and_hears_about_other_peoples_changes()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        Assert.True((await _engine.Follows.StateAsync("Deal", id, Ann)).Following);

        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "Won")), Bob);
        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "Lost")), Ann); // her own change tells nobody

        var inbox = await InboxAsync(Ann);
        var note = Assert.Single(inbox);
        Assert.Equal("Update", note.Type);
        Assert.Equal($"Bob Stone changed Deal {id}", note.Subject);
        Assert.Equal(("Deal", id), (note.DocType, note.DocName));
        Assert.False(note.Read);
        Assert.Empty(await InboxAsync(Bob));
    }

    [Fact]
    public async Task Following_needs_read_access_and_can_be_undone()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Follows.FollowAsync("Deal", id, Eve));
        await _engine.Follows.FollowAsync("Deal", id, Bob);
        await _engine.Follows.FollowAsync("Deal", id, Bob); // twice is the same as once
        var state = await _engine.Follows.StateAsync("Deal", id, Bob);
        Assert.True(state.Following);
        Assert.Equal(2, state.Followers);

        await _engine.Follows.UnfollowAsync("Deal", id, Bob);
        Assert.False((await _engine.Follows.StateAsync("Deal", id, Bob)).Following);
        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "x")), Ann);
        Assert.Empty(await InboxAsync(Bob));
    }

    [Fact]
    public async Task A_follower_who_can_no_longer_read_the_document_hears_nothing()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        await _engine.Follows.EnsureAsync("Deal", id, Eve.User); // followed once, long ago; she has no access now

        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "x")), Bob);

        Assert.Empty(await InboxAsync(Eve));
    }

    [Fact]
    public async Task Disabled_and_unknown_people_are_never_notified()
    {
        await UserAsync(Ann, "Ann Lee");
        await UserAsync(Bob, "Bob Stone", enabled: false);
        var id = await DealAsync(Ann);
        await _engine.Follows.EnsureAsync("Deal", id, Bob.User);
        await _engine.Follows.EnsureAsync("Deal", id, "ghost@example.com");

        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "x")), Ann);

        Assert.Empty(await InboxAsync(Bob));
        Assert.Empty(await _engine.Repository.ListAsync(_engine.Registry.Get("Notification Log"), new ListQuery { Filters = [new QueryFilter("for_user", FilterOp.Eq, "ghost@example.com")] }));
    }

    // ---- comments ----

    [Fact]
    public async Task A_mention_notifies_the_person_with_an_excerpt_and_followers_get_a_short_note()
    {
        await AllUsersAsync();
        var carol = new UserContext("carol@example.com", ["Clerk"]);
        await UserAsync(carol, "Carol");
        var id = await DealAsync(Ann);
        await _engine.Follows.FollowAsync("Deal", id, carol);

        await _engine.Activity.AddCommentAsync("Deal", id, "Please review @bob@example.com today", Ann);

        var bob = Assert.Single(await InboxAsync(Bob));
        Assert.Equal("Mention", bob.Type);
        Assert.Equal($"Ann Lee mentioned you in Deal {id}: Please review @bob@example.com today", bob.Subject);
        var follower = Assert.Single(await InboxAsync(carol));
        Assert.Equal("Comment", follower.Type);
        Assert.DoesNotContain("review", follower.Subject); // followers are told, not shown the text
        Assert.Empty(await InboxAsync(Ann));
        Assert.True((await _engine.Follows.StateAsync("Deal", id, Ann)).Following);
    }

    [Fact]
    public async Task A_mention_never_shows_a_comment_to_someone_who_cannot_read_the_document()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);

        await _engine.Activity.AddCommentAsync("Deal", id, "secret plan for @eve@example.com and @ghost@example.com", Ann);

        Assert.Empty(await InboxAsync(Eve));
    }

    [Fact]
    public async Task Mention_suggestions_match_name_or_email_and_only_offer_people_who_may_read_the_document()
    {
        await AllUsersAsync();
        await UserAsync(new UserContext("off@example.com", ["Clerk"]), "Bob Gone", enabled: false);
        var id = await DealAsync(Ann);

        var byName = await _engine.Activity.MentionCandidatesAsync("Deal", id, "bo", Ann);
        Assert.Equal(["bob@example.com"], byName.Select(o => o.Value));
        Assert.Equal("Bob Stone", byName[0].Label);

        Assert.Equal(["bob@example.com"], (await _engine.Activity.MentionCandidatesAsync("Deal", id, "bob@exa", Ann)).Select(o => o.Value));
        Assert.Empty(await _engine.Activity.MentionCandidatesAsync("Deal", id, "eve", Ann)); // cannot read the Deal
        Assert.DoesNotContain(await _engine.Activity.MentionCandidatesAsync("Deal", id, "", Ann), o => o.Value == Ann.User); // not yourself
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.MentionCandidatesAsync("Deal", id, "bo", Eve));
    }

    [Fact]
    public void Mentions_need_an_at_sign_and_are_limited()
    {
        Assert.Equal(["a@x.com"], Mentions.Find("hi @a@x.com and a@x.com again @A@X.com"));
        Assert.Empty(Mentions.Find("mail me at ann@example.com"));
        Assert.Empty(Mentions.Find("@nobody without a domain"));
        Assert.Equal(Mentions.Max, Mentions.Find(string.Join(' ', Enumerable.Range(0, 30).Select(i => $"@u{i}@x.com"))).Count);
    }

    // ---- notifications ----

    [Fact]
    public async Task People_only_see_and_clear_their_own_notifications()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        await _engine.Follows.FollowAsync("Deal", id, Bob);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "a")), Ann);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "b")), Ann);

        var inbox = await InboxAsync(Bob);
        Assert.Equal(2, inbox.Count);
        Assert.Equal(2, await _engine.Notifications.UnreadCountAsync(Bob));
        Assert.True(inbox[0].At > inbox[1].At); // newest first

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Notifications.MarkReadAsync(inbox[0].Id, Ann)); // not hers
        await _engine.Notifications.MarkReadAsync(inbox[0].Id, Bob);
        Assert.Equal(1, await _engine.Notifications.UnreadCountAsync(Bob));
        Assert.Single(await _engine.Notifications.ListAsync(Bob, unreadOnly: true, 50, null));

        await _engine.Notifications.MarkAllReadAsync(Bob);
        Assert.Equal(0, await _engine.Notifications.UnreadCountAsync(Bob));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Notifications.ListAsync(UserContext.Anonymous, false, 10, null));
    }

    [Fact]
    public async Task Sharing_tells_the_person()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);

        await _engine.ShareManager.SetAsync("Deal", id, Eve.User, true, false, Ann);

        var note = Assert.Single(await InboxAsync(Eve));
        Assert.Equal("Share", note.Type);
    }

    // ---- assignments ----

    [Fact]
    public async Task Assigning_records_it_notifies_follows_and_notes_it_in_the_thread()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);

        var todo = await _engine.Assignments.AssignAsync("Deal", id, Bob.User, "Call the client", "2026-12-31", "High", Ann);

        Assert.Equal("Bob Stone", todo.UserName);
        Assert.Equal("Ann Lee", todo.AssignedByName);
        Assert.Equal(("Open", "High", "2026-12-31"), (todo.Status, todo.Priority, todo.Due));
        Assert.Equal([todo.Id], (await _engine.Assignments.ForDocumentAsync("Deal", id, Ann)).Select(a => a.Id));
        var note = Assert.Single(await InboxAsync(Bob), n => n.Type == "Assignment");
        Assert.Equal($"Ann Lee assigned you Deal {id}: Call the client", note.Subject);
        Assert.True((await _engine.Follows.StateAsync("Deal", id, Bob)).Following);
        Assert.Contains((await _engine.Activity.TimelineAsync("Deal", id, Ann)), i => i.Content == "Assigned to Bob Stone");
    }

    [Fact]
    public async Task Assigning_gives_a_person_without_access_the_right_to_open_the_document()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Deal", id, Eve));

        await _engine.Assignments.AssignAsync("Deal", id, Eve.User, null, null, null, Ann);

        Assert.Equal("Big deal", (await _engine.Documents.GetAsync("Deal", id, Eve))["title"]); // read, through a share
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Deal", id, Values(("title", "x")), Eve));
        Assert.Contains(await InboxAsync(Eve), n => n.Type == "Assignment");
    }

    [Fact]
    public async Task Assigning_the_same_person_twice_changes_nothing()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);

        var first = await _engine.Assignments.AssignAsync("Deal", id, Bob.User, "one", null, null, Ann);
        var second = await _engine.Assignments.AssignAsync("Deal", id, Bob.User, "two", null, null, Ann);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(await _engine.Assignments.ForDocumentAsync("Deal", id, Ann));
    }

    [Fact]
    public async Task Bad_assignments_are_refused()
    {
        await AllUsersAsync();
        await UserAsync(new UserContext("off@example.com", ["Clerk"]), "Off", enabled: false);
        var id = await DealAsync(Ann);

        async Task Bad(string? user, string? text = null, string? date = null, string? priority = null) =>
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Assignments.AssignAsync("Deal", id, user, text, date, priority, Ann));

        await Bad("ghost@example.com");
        await Bad("");
        await Bad("off@example.com");
        await Bad(Bob.User, new string('x', AssignmentService.MaxDescription + 1));
        await Bad(Bob.User, "bell\a");
        await Bad(Bob.User, null, "31/12/2026");
        await Bad(Bob.User, null, null, "Urgent");
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Assignments.AssignAsync("Deal", id, Bob.User, null, null, null, Eve)); // cannot even read it
    }

    [Fact]
    public async Task Only_people_who_could_share_the_document_can_assign_it()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        await _engine.ShareManager.SetAsync("Deal", id, Eve.User, true, true, Ann); // Eve may edit through a share, which does not let her hand it on

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Assignments.AssignAsync("Deal", id, Bob.User, null, null, null, Eve));
    }

    [Fact]
    public async Task The_assignee_completes_it_and_the_assigner_is_told()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        var todo = await _engine.Assignments.AssignAsync("Deal", id, Bob.User, "Call", null, null, Ann);

        Assert.Equal(["Open"], (await _engine.Assignments.MineAsync(Bob, "Open")).Select(a => a.Status));
        var done = await _engine.Assignments.CompleteAsync(todo.Id, Bob);

        Assert.Equal("Closed", done.Status);
        Assert.Empty(await _engine.Assignments.MineAsync(Bob, "Open"));
        Assert.Equal(["Closed"], (await _engine.Assignments.MineAsync(Bob, "Closed")).Select(a => a.Status));
        Assert.Contains(await InboxAsync(Ann), n => n.Subject.Contains("completed the assignment"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Assignments.CompleteAsync(todo.Id, Bob)); // already done
    }

    [Fact]
    public async Task Only_the_two_people_involved_can_touch_an_assignment()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        var todo = await _engine.Assignments.AssignAsync("Deal", id, Bob.User, null, null, null, Ann);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Assignments.CompleteAsync(todo.Id, Eve));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Assignments.RemoveAsync(todo.Id, Eve));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Assignments.CompleteAsync("nope", Bob));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Assignments.MineAsync(UserContext.Anonymous, "Open"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Assignments.MineAsync(Bob, "Cancelled"));

        var removed = await _engine.Assignments.RemoveAsync(todo.Id, Ann); // the assigner takes it back
        Assert.Equal("Cancelled", removed.Status);
        Assert.Empty(await _engine.Assignments.ForDocumentAsync("Deal", id, Ann));
    }

    [Fact]
    public async Task The_assignment_list_of_a_document_needs_read_access()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Assignments.ForDocumentAsync("Deal", id, Eve));
    }

    // ---- clean-up ----

    [Fact]
    public async Task Deleting_a_document_removes_its_assignments_followers_and_notifications()
    {
        await AllUsersAsync();
        var id = await DealAsync(Ann);
        await _engine.Assignments.AssignAsync("Deal", id, Bob.User, "x", null, null, Ann);
        await _engine.Documents.UpdateAsync("Deal", id, Values(("status", "a")), Bob); // notifies Ann
        Assert.NotEmpty(await InboxAsync(Ann));

        await _engine.Documents.DeleteAsync("Deal", id, Ann);

        Assert.Empty(await InboxAsync(Ann));
        Assert.Empty(await InboxAsync(Bob));
        Assert.Empty(await _engine.Assignments.MineAsync(Bob, "Open"));
        Assert.Empty(await _engine.Follows.FollowersAsync("Deal", id));
    }

    [Fact]
    public async Task Renaming_a_document_keeps_its_assignments_and_followers()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Binder", "autoname": "field:label", "allowRename": true, "fields": [ { "name": "label", "type": "Data", "required": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await AllUsersAsync();
        await _engine.Documents.InsertAsync("Binder", Values(("label", "Old")), Ann);
        await _engine.Assignments.AssignAsync("Binder", "Old", Bob.User, "x", null, null, Ann);

        await _engine.Documents.RenameAsync("Binder", "Old", "New", false, Ann);

        Assert.Equal("New", Assert.Single(await _engine.Assignments.MineAsync(Bob, "Open")).DocName);
        Assert.True((await _engine.Follows.StateAsync("Binder", "New", Ann)).Following);
        Assert.Equal("New", (await InboxAsync(Bob)).First().DocName);
    }
}
