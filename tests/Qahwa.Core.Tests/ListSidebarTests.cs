using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>The list sidebar (group-by counts), status colours and comment counts.</summary>
public class ListSidebarTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);   // own documents only
    private static readonly UserContext Boss = new("boss@example.com", ["Boss"]);  // everything, plus level 1

    public ListSidebarTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Ticket", "statusField": "stage",
              "indicators": [ { "value": "Open", "color": "orange" }, { "value": "Done", "color": "green" } ],
              "fields": [
                { "name": "title", "type": "Data" }, { "name": "stage", "type": "Select", "options": "Open\nDone\nLater" },
                { "name": "urgent", "type": "Check" }, { "name": "notes", "type": "LongText" },
                { "name": "cost", "type": "Currency", "permLevel": 1 }, { "name": "tier", "type": "Select", "options": "A\nB", "permLevel": 1 }
              ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "ifOwner": true },
                { "role": "Boss", "read": true, "write": true, "create": true, "delete": true },
                { "role": "Boss", "read": true, "write": true, "permLevel": 1 }
              ]
            }
            """));
    }

    private async Task<string> TicketAsync(UserContext by, string? stage, bool urgent = false, string title = "T") =>
        (string)(await _engine.Documents.InsertAsync("Ticket", Values(("title", title), ("stage", stage), ("urgent", urgent)), by))["name"]!;

    private async Task<Dictionary<string, long>> CountsAsync(UserContext user, string field, ListRequest? request = null) =>
        (await _engine.Documents.GroupByAsync("Ticket", field, request ?? new ListRequest(), 20, user))
            .ToDictionary(g => g.Value ?? "(empty)", g => g.Count);

    // ---- group-by ----

    [Fact]
    public async Task Counts_are_per_value_most_common_first_with_empty_as_null()
    {
        foreach (var stage in new[] { "Open", "Open", "Open", "Done", "Done", null })
            await TicketAsync(Boss, stage);

        var groups = await _engine.Documents.GroupByAsync("Ticket", "stage", new ListRequest(), 20, Boss);

        Assert.Equal([("Open", 3L), ("Done", 2L), (null, 1L)], groups.Select(g => (g.Value, g.Count)));
    }

    [Fact]
    public async Task Counts_follow_the_filters_of_the_list_and_check_fields_read_as_1_and_0()
    {
        await TicketAsync(Boss, "Open", urgent: true);
        await TicketAsync(Boss, "Open");
        await TicketAsync(Boss, "Done", urgent: true);

        var inOpen = await CountsAsync(Boss, "urgent", new ListRequest { Filters = [new("stage", "=", "Open")] });

        Assert.Equal(new Dictionary<string, long> { ["1"] = 1, ["0"] = 1 }, inOpen);
        Assert.Equal(2, (await CountsAsync(Boss, "stage"))["Open"]);
    }

    [Fact]
    public async Task A_person_only_counts_what_they_could_list()
    {
        await TicketAsync(Ann, "Open");
        await TicketAsync(Boss, "Open");
        await TicketAsync(Boss, "Done");

        Assert.Equal(new Dictionary<string, long> { ["Open"] = 1 }, await CountsAsync(Ann, "stage")); // own-only role
        Assert.Equal(2, (await CountsAsync(Boss, "stage"))["Open"]);
    }

    [Fact]
    public async Task Shared_documents_and_user_permissions_shape_the_counts_like_the_list()
    {
        var bobs = await TicketAsync(Boss, "Done");
        await _engine.Documents.InsertAsync("User", Values(("email", Boss.User), ("full_name", "Boss")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", Ann.User), ("full_name", "Ann")), TestUsers.Admin);
        await TicketAsync(Ann, "Open");

        await _engine.ShareManager.SetAsync("Ticket", bobs, Ann.User, true, false, Boss);

        Assert.Equal(new Dictionary<string, long> { ["Open"] = 1, ["Done"] = 1 }, await CountsAsync(Ann, "stage"));
    }

    [Fact]
    public async Task Only_fields_the_person_may_read_and_that_group_sensibly_can_be_used()
    {
        await TicketAsync(Boss, "Open");

        foreach (var field in new[] { "notes", "cost", "ghost", "name", "creation", "x\"; DROP TABLE tabTicket;--", "", null })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.GroupByAsync("Ticket", field!, new ListRequest(), 20, Ann));
        await _engine.Documents.GroupByAsync("Ticket", "tier", new ListRequest(), 20, Boss); // level 1 for someone who may read it
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.GroupByAsync("Ticket", "tier", new ListRequest(), 20, Ann));
        Assert.NotEmpty(await _engine.Documents.GroupByAsync("Ticket", "owner", new ListRequest(), 20, Boss));
        Assert.NotEmpty(await _engine.Documents.GroupByAsync("Ticket", "docstatus", new ListRequest(), 20, Boss));
    }

    [Fact]
    public async Task Filters_on_hidden_fields_are_still_refused_and_access_is_required()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.GroupByAsync("Ticket", "stage", new ListRequest { Filters = [new("cost", ">", "1")] }, 20, Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GroupByAsync("Ticket", "stage", new ListRequest(), 20, TestUsers.Nobody));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GroupByAsync("Nope", "stage", new ListRequest(), 20, Boss));
    }

    [Fact]
    public async Task The_number_of_groups_is_limited()
    {
        for (var i = 0; i < 5; i++)
            await TicketAsync(Boss, null, title: $"t{i}");
        for (var i = 0; i < 3; i++)
            await _engine.Documents.InsertAsync("Ticket", Values(("title", $"x{i}")), Boss);

        Assert.Single(await _engine.Documents.GroupByAsync("Ticket", "title", new ListRequest(), 1, Boss));
        Assert.True((await _engine.Documents.GroupByAsync("Ticket", "title", new ListRequest(), 10_000, Boss)).Count <= DocumentService.MaxGroups);
    }

    // ---- comment counts ----

    [Fact]
    public async Task Comment_counts_only_count_real_comments_on_readable_documents()
    {
        var mine = await TicketAsync(Ann, "Open");
        var theirs = await TicketAsync(Boss, "Open");
        await _engine.Activity.AddCommentAsync("Ticket", mine, "one", Ann);
        await _engine.Activity.AddCommentAsync("Ticket", mine, "two", Ann);
        await _engine.Activity.AddCommentAsync("Ticket", theirs, "private talk", Boss);
        await _engine.Documents.InsertAsync("User", Values(("email", Ann.User), ("full_name", "Ann")), TestUsers.Admin);
        await _engine.ShareManager.SetAsync("Ticket", mine, "ghost@example.com", true, false, Ann).ContinueWith(_ => { }); // a system note, which is not a comment

        var counts = await _engine.Activity.CommentCountsAsync("Ticket", [mine, theirs, "MISSING"], Ann);

        Assert.Equal(new Dictionary<string, long> { [mine] = 2 }, counts); // the Boss's document is not hers to know about
        Assert.Equal(1, (await _engine.Activity.CommentCountsAsync("Ticket", [mine, theirs], Boss))[theirs]);
    }

    [Fact]
    public async Task Comment_counts_handle_empty_unknown_and_oversized_requests()
    {
        var id = await TicketAsync(Boss, "Open");
        await _engine.Activity.AddCommentAsync("Ticket", id, "x", Boss);

        Assert.Empty(await _engine.Activity.CommentCountsAsync("Ticket", [], Boss));
        Assert.Empty(await _engine.Activity.CommentCountsAsync("Ticket", ["", "nope"], Boss));
        Assert.Equal(1, (await _engine.Activity.CommentCountsAsync("Ticket", Enumerable.Repeat(id, 500).Append("x").ToList(), Boss))[id]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Activity.CommentCountsAsync("Ticket", [id], TestUsers.Nobody));
    }

    // ---- status colours ----

    [Fact]
    public void Status_colours_are_checked()
    {
        void Bad(string json) => Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
        const string Field = """{ "name": "stage", "type": "Select", "options": "A\nB" }""";

        Bad($$"""{ "name": "X", "statusField": "nope", "fields": [ {{Field}} ] }""");
        Bad("""{ "name": "X", "statusField": "n", "fields": [ { "name": "n", "type": "Int" } ] }""");
        Bad($$"""{ "name": "X", "indicators": [ { "value": "A", "color": "red" } ], "fields": [ {{Field}} ] }""");
        Bad($$"""{ "name": "X", "statusField": "stage", "indicators": [ { "value": "A", "color": "pink" } ], "fields": [ {{Field}} ] }""");
        Bad($$"""{ "name": "X", "statusField": "stage", "indicators": [ { "value": "A", "color": "red" }, { "value": "A", "color": "blue" } ], "fields": [ {{Field}} ] }""");
        Bad($$"""{ "name": "X", "statusField": "stage", "indicators": [ { "value": " ", "color": "red" } ], "fields": [ {{Field}} ] }""");
        Bad($$"""{ "name": "X", "statusField": "stage", "indicators": [ {{string.Join(",", Enumerable.Range(0, DocTypeLoader.MaxIndicators + 1).Select(i => $$"""{ "value": "v{{i}}", "color": "red" }"""))}} ], "fields": [ {{Field}} ] }""");

        var good = DocTypeLoader.Parse($$"""{ "name": "X", "statusField": "stage", "indicators": [ { "value": "A", "color": "green" } ], "fields": [ {{Field}} ] }""");
        var again = DocTypeLoader.Parse(DocTypeLoader.Serialize(good));
        Assert.Equal("stage", again.StatusField);
        Assert.Equal(("A", "green"), (again.Indicators[0].Value, again.Indicators[0].Color));
    }
}
