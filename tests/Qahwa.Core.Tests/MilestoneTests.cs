using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class MilestoneTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk", UserContext.AllRole]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk", UserContext.AllRole]);
    private static readonly UserContext Nosy = new("nosy@example.com", ["Nosy", UserContext.AllRole]);

    public MilestoneTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "autoname": "TKT-.####", "fields": [
                { "name": "subject", "type": "Data" },
                { "name": "status", "type": "Select", "options": "Open\nIn Progress\nClosed", "default": "Open" },
                { "name": "secret_level", "type": "Data", "permLevel": 1 },
                { "name": "pass", "type": "Password" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true },
                { "role": "Nosy", "read": true } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quiet", "trackChanges": false, "fields": [ { "name": "status", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private Task TrackAsync(string doctype, string field) =>
        _engine.Documents.InsertAsync("Milestone Tracker", Values(("document_type", doctype), ("track_field", field)), TestUsers.SystemManager);

    private async Task<string> TicketAsync() => (string)(await _engine.Documents.InsertAsync("Ticket", Values(("subject", "Printer")), Ann))["name"]!;

    private Task MoveAsync(string name, string status, UserContext by) =>
        _engine.Documents.UpdateAsync("Ticket", name, Values(("status", status)), by);

    [Fact]
    public async Task A_tracked_field_shows_each_value_it_held_with_who_and_how_long()
    {
        await TrackAsync("Ticket", "status");
        var name = await TicketAsync();
        _engine.Clock.Advance(TimeSpan.FromHours(2));
        await MoveAsync(name, "In Progress", Bob);
        _engine.Clock.Advance(TimeSpan.FromDays(1));
        await MoveAsync(name, "Closed", Ann);

        var view = (await _engine.Milestones.ForDocumentAsync("Ticket", name, Ann))!;

        Assert.Equal("status", view.Field);
        Assert.Equal(["Open", "In Progress", "Closed"], view.Items.Select(i => i.Value));
        Assert.Equal([Ann.User, Bob.User, Ann.User], view.Items.Select(i => i.By));
        Assert.Null(view.Items[0].AfterSeconds);
        Assert.Equal(2 * 3600, view.Items[1].AfterSeconds);
        Assert.Equal(24 * 3600, view.Items[2].AfterSeconds);
    }

    [Fact]
    public async Task A_document_that_never_changed_shows_its_starting_value_only()
    {
        await TrackAsync("Ticket", "status");
        var name = await TicketAsync();

        var view = (await _engine.Milestones.ForDocumentAsync("Ticket", name, Ann))!;

        var only = Assert.Single(view.Items);
        Assert.Equal("Open", only.Value);
        Assert.Equal(Ann.User, only.By);
    }

    [Fact]
    public async Task Changes_to_other_fields_and_saves_without_a_change_are_not_milestones()
    {
        await TrackAsync("Ticket", "status");
        var name = await TicketAsync();
        await _engine.Documents.UpdateAsync("Ticket", name, Values(("subject", "Printer jam")), Ann);
        await MoveAsync(name, "Open", Ann); // the same value

        Assert.Single((await _engine.Milestones.ForDocumentAsync("Ticket", name, Ann))!.Items);
    }

    [Fact]
    public async Task A_doctype_with_no_tracker_has_no_milestones()
    {
        var name = await TicketAsync();
        Assert.Null(await _engine.Milestones.ForDocumentAsync("Ticket", name, Ann));
    }

    [Fact]
    public async Task Only_people_who_can_read_the_document_see_them()
    {
        await TrackAsync("Ticket", "status");
        var name = await TicketAsync();

        Assert.NotNull(await _engine.Milestones.ForDocumentAsync("Ticket", name, Nosy));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Milestones.ForDocumentAsync("Ticket", name, UserContext.Anonymous));
    }

    [Fact]
    public async Task A_field_the_reader_may_not_see_gives_nothing()
    {
        await TrackAsync("Ticket", "secret_level");
        var name = await TicketAsync();

        Assert.Null(await _engine.Milestones.ForDocumentAsync("Ticket", name, Nosy));
    }

    [Theory]
    [InlineData("Nope", "status", "document_type")]
    [InlineData("Ticket", "missing", "track_field")]
    [InlineData("Ticket", "pass", "track_field")]
    [InlineData("Quiet", "status", "document_type")]
    public async Task A_tracker_must_name_a_trackable_field_of_a_logged_doctype(string doctype, string field, string errorField)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => TrackAsync(doctype, field));
        var errors = ex is DocumentValidationException v ? v.Errors.Select(e => e.Field).ToList() : [];
        Assert.Contains(errorField, errors);
    }

    [Fact]
    public async Task Only_a_system_manager_can_set_up_trackers()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.InsertAsync("Milestone Tracker", Values(("document_type", "Ticket"), ("track_field", "status")), Ann));
    }
}
