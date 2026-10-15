using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AssignmentRuleTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Cara = new("cara@example.com", ["Clerk"]);
    private static readonly UserContext Dan = new("dan@example.com", ["Clerk"]); // disabled
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]); // cannot read Ticket on her own

    public AssignmentRuleTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Ticket", "fields": [
                { "name": "title", "type": "Data" }, { "name": "urgency", "type": "Data" },
                { "name": "status", "type": "Data" }, { "name": "handler", "type": "Data" }, { "name": "notes", "type": "Text" } ],
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

    private async Task PeopleAsync()
    {
        await UserAsync(Ann, "Ann Lee");
        await UserAsync(Bob, "Bob Stone");
        await UserAsync(Cara, "Cara Hill");
        await UserAsync(Dan, "Dan Gone", enabled: false);
        await UserAsync(Eve, "Eve Outsider");
    }

    private static AssignmentRule Rule(string name = "Support", string strategy = AssignmentRule.RoundRobin, params string[] users) =>
        new() { Name = name, Strategy = strategy, Users = users };

    private Task<AssignmentRuleView> SaveAsync(params AssignmentRule[] rules) =>
        _engine.AssignmentRuleManager.SaveAsync("Ticket", new AssignmentRuleSet { DocType = "Ticket", Rules = rules }, null, TestUsers.SystemManager);

    private async Task<string> TicketAsync(params (string, object?)[] values)
    {
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        return (string)(await _engine.Documents.InsertAsync("Ticket", Values([("title", "Printer"), .. values]), Ann))["name"]!;
    }

    private async Task<IReadOnlyList<string>> AssigneesAsync(string id) =>
        (await _engine.Assignments.ForDocumentAsync("Ticket", id, Ann)).Where(a => a.Status == "Open").Select(a => a.User).ToList();

    // ---- picking people ----

    [Fact]
    public async Task Round_robin_takes_people_in_turn_and_skips_disabled_users()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: [Bob.User, Dan.User, Cara.User]));

        var picked = new List<string>();
        for (var i = 0; i < 5; i++)
            picked.Add(Assert.Single(await AssigneesAsync(await TicketAsync())));

        Assert.Equal([Bob.User, Cara.User, Bob.User, Cara.User, Bob.User], picked);
    }

    [Fact]
    public async Task Load_balancing_gives_the_document_to_whoever_has_the_fewest_open_assignments()
    {
        await PeopleAsync();
        await SaveAsync(Rule(strategy: AssignmentRule.LoadBalancing, users: [Bob.User, Cara.User]));
        var busy = await _engine.Documents.InsertAsync("Ticket", Values(("title", "Other")), Ann);
        await _engine.Assignments.AssignAsync("Ticket", (string)busy["name"]!, Bob.User, null, null, null, Ann);

        // Bob already has one open assignment, Cara none: she gets the first; then they are level and the list order decides; then Cara is behind again.
        var picked = new List<string>();
        for (var i = 0; i < 3; i++)
            picked.Add(Assert.Single(await AssigneesAsync(await TicketAsync())));
        Assert.Equal([Cara.User, Bob.User, Cara.User], picked);
    }

    [Fact]
    public async Task A_field_rule_assigns_the_user_named_in_the_document()
    {
        await PeopleAsync();
        await SaveAsync(new AssignmentRule { Name = "Handler", Strategy = AssignmentRule.Field, UserField = "handler" });

        Assert.Equal([Cara.User], await AssigneesAsync(await TicketAsync(("handler", Cara.User))));
        Assert.Empty(await AssigneesAsync(await TicketAsync(("handler", "nobody@example.com")))); // unknown people are ignored
        Assert.Empty(await AssigneesAsync(await TicketAsync(("handler", Dan.User)))); // so are disabled ones
        Assert.Empty(await AssigneesAsync(await TicketAsync()));
    }

    [Fact]
    public async Task A_condition_chooses_which_documents_a_rule_applies_to_and_the_first_matching_rule_wins()
    {
        await PeopleAsync();
        await SaveAsync(
            Rule("Urgent", users: Bob.User) with { Condition = "urgency == 'High'" },
            Rule("Everything else", users: Cara.User));

        Assert.Equal([Bob.User], await AssigneesAsync(await TicketAsync(("urgency", "High"))));
        Assert.Equal([Cara.User], await AssigneesAsync(await TicketAsync(("urgency", "Low"))));
    }

    [Fact]
    public async Task A_document_is_given_away_once_even_when_it_is_saved_again_or_the_assignment_is_closed()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: [Bob.User, Cara.User]));
        var id = await TicketAsync();
        var todo = Assert.Single(await _engine.Assignments.ForDocumentAsync("Ticket", id, Ann));

        await _engine.Documents.UpdateAsync("Ticket", id, Values(("status", "Working")), Ann);
        await _engine.Assignments.CompleteAsync(todo.Id, new UserContext(todo.User, ["Clerk"]));
        await _engine.Documents.UpdateAsync("Ticket", id, Values(("status", "Still working")), Ann);

        Assert.Single(await _engine.Assignments.ForDocumentAsync("Ticket", id, Ann));
    }

    [Fact]
    public async Task A_rule_can_start_working_after_the_document_changes()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Bob.User) with { Condition = "status == 'Escalated'" });
        var id = await TicketAsync();
        Assert.Empty(await AssigneesAsync(id));

        await _engine.Documents.UpdateAsync("Ticket", id, Values(("status", "Escalated")), Ann);

        Assert.Equal([Bob.User], await AssigneesAsync(id));
    }

    [Fact]
    public async Task A_close_condition_withdraws_the_assignment_and_says_so()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Bob.User) with { CloseCondition = "status == 'Resolved'" });
        var id = await TicketAsync();
        Assert.Equal([Bob.User], await AssigneesAsync(id));

        await _engine.Documents.UpdateAsync("Ticket", id, Values(("status", "Resolved")), Ann);

        Assert.Empty(await AssigneesAsync(id));
        Assert.Contains(await _engine.Activity.TimelineAsync("Ticket", id, Ann), i => i.Content == "Assignment of Bob Stone withdrawn by the rule \"Support\"");
        Assert.Empty(await _engine.Assignments.MineAsync(Bob, "Open"));
    }

    // ---- what the assignee gets ----

    [Fact]
    public async Task The_assignee_is_told_follows_and_can_open_the_document_with_the_rules_details()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Eve.User) with { Description = "Please look at this", Priority = "High", DueInDays = 3 });
        var id = await TicketAsync();

        var mine = Assert.Single(await _engine.Assignments.MineAsync(Eve, "Open"));
        Assert.Equal(("Please look at this", "High"), (mine.Description, mine.Priority));
        Assert.Equal(DateOnly.FromDateTime(_engine.Clock.GetUtcNow().UtcDateTime).AddDays(3).ToString("yyyy-MM-dd"), mine.Due);
        Assert.Equal("Printer", (await _engine.Documents.GetAsync("Ticket", id, Eve))["title"]); // she may open it
        var note = Assert.Single(await _engine.Notifications.ListAsync(Eve, unreadOnly: false, limit: 50, before: null), n => n.Type == "Assignment");
        Assert.Contains("by the rule \"Support\"", note.Subject);
        Assert.True((await _engine.Follows.StateAsync("Ticket", id, Eve)).Following);
    }

    [Fact]
    public async Task A_rule_that_cannot_pick_anyone_never_stops_a_document_from_being_saved()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Dan.User)); // the only person is disabled

        var id = await TicketAsync();

        Assert.Empty(await AssigneesAsync(id));
        Assert.Equal("Printer", (await _engine.Documents.GetAsync("Ticket", id, Ann))["title"]);
    }

    [Fact]
    public async Task Switched_off_and_deleted_rules_do_nothing()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Bob.User) with { Active = false });
        Assert.Empty(await AssigneesAsync(await TicketAsync()));

        var view = await _engine.AssignmentRuleManager.GetAsync("Ticket", TestUsers.SystemManager);
        await _engine.AssignmentRuleManager.SaveAsync("Ticket", view.Set with { Rules = [view.Set.Rules[0] with { Active = true }] }, view.Modified, TestUsers.SystemManager);
        Assert.Single(await AssigneesAsync(await TicketAsync()));

        await _engine.AssignmentRuleManager.DeleteAsync("Ticket", TestUsers.SystemManager);
        Assert.Empty(await AssigneesAsync(await TicketAsync()));
    }

    // ---- the rules themselves ----

    [Fact]
    public async Task Problems_are_reported_by_path()
    {
        await PeopleAsync();
        var errors = (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(
            Rule("", users: Bob.User),
            Rule("Dup", users: "ghost@example.com"),
            Rule("dup", users: Bob.User),
            Rule("NoUsers", users: []),
            Rule("Odd", strategy: "Random", users: Bob.User),
            new AssignmentRule { Name = "F", Strategy = AssignmentRule.Field, UserField = "title2" },
            Rule("Cond", users: Bob.User) with { Condition = "nope = 1", CloseCondition = "1 +", Priority = "Urgent", DueInDays = 999 },
            Rule("Text", users: Bob.User) with { Description = "bad\u0001text" }))).Errors.ToDictionary(e => e.Field, e => e.Message);

        Assert.Contains("rules[0].name", errors.Keys);
        Assert.Contains("rules[1].users", errors.Keys);
        Assert.Contains("rules[2].name", errors.Keys);
        Assert.Contains("rules[3].users", errors.Keys);
        Assert.Contains("rules[4].strategy", errors.Keys);
        Assert.Contains("rules[5].userField", errors.Keys);
        Assert.Equal(["rules[6].closeCondition", "rules[6].condition", "rules[6].dueInDays", "rules[6].priority"], errors.Keys.Where(k => k.StartsWith("rules[6]")).Order());
        Assert.Contains("rules[7].description", errors.Keys);
    }

    [Fact]
    public async Task At_most_twenty_rules_and_fifty_people_per_rule()
    {
        await PeopleAsync();
        var tooMany = Enumerable.Range(0, 21).Select(i => Rule($"R{i}", users: Bob.User)).ToArray();
        Assert.Contains("rules", (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(tooMany))).Errors.Select(e => e.Field));

        var crowd = Enumerable.Range(0, 51).Select(i => $"u{i}@example.com").ToArray();
        Assert.Contains("rules[0].users", (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(Rule(users: crowd)))).Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task Only_system_managers_manage_rules_and_only_for_ordinary_doctypes()
    {
        await PeopleAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.AssignmentRuleManager.ListAsync(Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.AssignmentRuleManager.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AssignmentRuleManager.GetAsync("ToDo", TestUsers.SystemManager));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.AssignmentRuleManager.GetAsync("Nope", TestUsers.SystemManager));

        var items = await _engine.AssignmentRuleManager.ListAsync(TestUsers.SystemManager);
        Assert.Contains(items, i => i.DocType == "Ticket");
        Assert.DoesNotContain(items, i => i.DocType is "ToDo" or "User");
    }

    [Fact]
    public async Task A_stale_save_is_a_conflict_and_rules_survive_a_restart()
    {
        await PeopleAsync();
        var saved = await SaveAsync(Rule(users: Bob.User));
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(Rule(users: Cara.User)));

        var restarted = new AssignmentRuleCatalog();
        var manager = new AssignmentRuleManager(_engine.Registry, _engine.AssignmentRuleStore, restarted, _engine.Repository, _engine.Permissions, _engine.Clock);
        Assert.Empty(await manager.LoadAsync());
        Assert.Single(restarted.For("Ticket")!.Rules);
        Assert.NotNull(saved.Modified);
    }

    [Fact]
    public async Task Field_names_only_offer_fields_that_can_hold_a_user()
    {
        var view = await _engine.AssignmentRuleManager.GetAsync("Ticket", TestUsers.SystemManager);
        Assert.Contains("handler", view.UserFields);
        Assert.DoesNotContain("notes", view.UserFields);
    }
}
