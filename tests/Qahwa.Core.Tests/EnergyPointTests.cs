using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Energy points: rules that give recognition for work, a log, and a leaderboard (ADR-159).</summary>
public class EnergyPointTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);

    public EnergyPointTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "isSubmittable": false, "fields": [
                { "name": "subject", "type": "Data" },
                { "name": "status", "type": "Select", "options": "Open\nClosed", "default": "Open" },
                { "name": "fixer", "type": "Link", "options": "User" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task PeopleAsync()
    {
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        foreach (var (email, name) in new[] { ("ann@example.com", "Ann Smith"), ("bob@example.com", "Bob Jones") })
            await _engine.Documents.InsertAsync("User", Values(("email", email), ("full_name", name), ("enabled", true)), TestUsers.Admin);
    }

    private Task RuleAsync(string name, string @event, int points, string? condition = null, string? watch = null, string who = "owner", bool once = true, int perDay = 10) =>
        _engine.Documents.InsertAsync("Energy Point Rule", Values(("rule_name", name), ("reference_doctype", "Ticket"), ("event", @event), ("points", points), ("condition", condition),
            ("watch_field", watch), ("user_field", who), ("once_per_document", once), ("max_per_day", perDay)), TestUsers.Admin);

    private async Task<string> TicketAsync(UserContext by, string subject = "Printer") =>
        (string)(await _engine.Documents.InsertAsync("Ticket", Values(("subject", subject)), by))["name"]!;

    [Fact]
    public async Task A_new_document_gives_its_owner_the_points_of_the_rule()
    {
        await PeopleAsync();
        await RuleAsync("Raised a ticket", "New", 5);

        await TicketAsync(Ann);

        var mine = await _engine.EnergyPoints.MineAsync(Ann);
        Assert.Equal((5, 5), (mine.Total, mine.ThisWeek));
        Assert.StartsWith("Raised a ticket: Ticket ", Assert.Single(mine.Recent).Reason, StringComparison.Ordinal);
        Assert.Equal(0, (await _engine.EnergyPoints.MineAsync(Bob)).Total);
    }

    [Fact]
    public async Task A_value_change_with_a_condition_goes_to_the_person_a_field_names()
    {
        await PeopleAsync();
        await RuleAsync("Fixed a ticket", "ValueChange", 10, condition: "status == 'Closed'", watch: "status", who: "fixer");
        var id = await TicketAsync(Ann);

        await _engine.Documents.UpdateAsync("Ticket", id, Values(("fixer", "bob@example.com")), Ann);        // nothing watched changed
        Assert.Equal(0, (await _engine.EnergyPoints.MineAsync(Bob)).Total);
        await _engine.Documents.UpdateAsync("Ticket", id, Values(("status", "Closed")), Ann);

        Assert.Equal(10, (await _engine.EnergyPoints.MineAsync(Bob)).Total);   // the fixer, not the person who clicked
        Assert.Equal(0, (await _engine.EnergyPoints.MineAsync(Ann)).Total);
    }

    [Fact]
    public async Task Points_cannot_be_farmed_once_for_each_document_and_a_daily_limit()
    {
        await PeopleAsync();
        await RuleAsync("Touched a ticket", "Update", 1, once: true);
        var id = await TicketAsync(Ann);
        await _engine.Documents.UpdateAsync("Ticket", id, Values(("subject", "One")), Ann);
        _engine.Clock.Advance(TimeSpan.FromSeconds(2));
        await _engine.Documents.UpdateAsync("Ticket", id, Values(("subject", "Two")), Ann);
        Assert.Equal(1, (await _engine.EnergyPoints.MineAsync(Ann)).Total);              // the same document pays once

        await RuleAsync("Daily cap", "New", 2, once: false, perDay: 2);
        for (var i = 0; i < 4; i++)
            await TicketAsync(Ann, $"T{i}");
        Assert.Equal(1 + 2 * 2, (await _engine.EnergyPoints.MineAsync(Ann)).Total);        // two awards of the capped rule, however many tickets
    }

    [Fact]
    public async Task The_leaderboard_ranks_names_only_and_marks_you()
    {
        await PeopleAsync();
        await RuleAsync("Raised a ticket", "New", 5, once: false, perDay: 0);
        await TicketAsync(Ann);
        await TicketAsync(Bob);
        await TicketAsync(Bob, "Scanner");

        var board = await _engine.EnergyPoints.LeaderboardAsync("all", Ann);

        Assert.Equal([("Bob Jones", 10, false), ("Ann Smith", 5, true)], board.Select(b => (b.FullName, b.Points, b.You)));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.EnergyPoints.LeaderboardAsync("decade", Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.EnergyPoints.LeaderboardAsync("all", UserContext.Anonymous));
    }

    [Fact]
    public async Task A_disabled_rule_and_a_switched_off_account_get_nothing()
    {
        await PeopleAsync();
        await RuleAsync("Raised a ticket", "New", 5);
        await _engine.Documents.UpdateAsync("Energy Point Rule", "Raised a ticket", Values(("enabled", false)), TestUsers.Admin);
        await TicketAsync(Ann);
        Assert.Equal(0, (await _engine.EnergyPoints.MineAsync(Ann)).Total);

        await _engine.Documents.UpdateAsync("Energy Point Rule", "Raised a ticket", Values(("enabled", true)), TestUsers.Admin);
        await _engine.Documents.UpdateAsync("User", "ann@example.com", Values(("enabled", false)), TestUsers.Admin);
        await TicketAsync(Ann, "Again");
        Assert.Equal(0, (await _engine.EnergyPoints.MineAsync(Ann)).Total);
    }

    [Fact]
    public async Task Rules_must_be_sensible_and_only_system_managers_write_them_or_the_log()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => RuleAsync("Zero", "New", 0));
        await Assert.ThrowsAsync<DocumentValidationException>(() => RuleAsync("Huge", "New", 500));
        await Assert.ThrowsAsync<DocumentValidationException>(() => RuleAsync("No watch", "ValueChange", 3));
        await Assert.ThrowsAsync<DocumentValidationException>(() => RuleAsync("Bad user", "New", 3, who: "subject"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Energy Point Log", Values(("user", "ann@example.com"), ("points", 99)), Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.InsertAsync("Energy Point Rule", Values(("rule_name", "Mine"), ("reference_doctype", "Ticket"), ("points", 5)), Ann));
    }
}
