using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class NotificationRuleTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk", "Auditor"]);
    private static readonly UserContext Cara = new("cara@example.com", ["Clerk", "Auditor"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]); // cannot read Claim

    public NotificationRuleTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Claim", "isSubmittable": true, "fields": [
                { "name": "title", "type": "Data" }, { "name": "status", "type": "Data" }, { "name": "amount", "type": "Currency" },
                { "name": "handler", "type": "Data" }, { "name": "due_on", "type": "Date" }, { "name": "notes", "type": "Text" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "submit": true, "cancel": true, "delete": true } ] }
            """));
    }

    private async Task UserAsync(UserContext person)
    {
        foreach (var role in person.Roles)
        {
            if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), role))
                await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
        }

        await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User[..person.User.IndexOf('@')]), ("enabled", true)), TestUsers.Admin);
        foreach (var role in person.Roles)
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
    }

    private async Task PeopleAsync()
    {
        foreach (var person in new[] { Ann, Bob, Cara, Eve })
            await UserAsync(person);
    }

    private static NotificationRule Rule(string name = "Tell Bob", string @event = NotificationRule.New, string message = "{{doctype}} {{name}}: {{title}}", params string[] users) =>
        new() { Name = name, Event = @event, Message = message, Users = users };

    private Task<NotificationRuleView> SaveAsync(params NotificationRule[] rules) =>
        _engine.NotificationRuleManager.SaveAsync("Claim", new NotificationRuleSet { DocType = "Claim", Rules = rules }, null, TestUsers.SystemManager);

    private async Task<string> ClaimAsync(UserContext by, params (string, object?)[] values)
    {
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        var input = Values(("title", "Taxi"));
        foreach (var (key, value) in values)
            input[key] = value;
        return (string)(await _engine.Documents.InsertAsync("Claim", input, by))["name"]!;
    }

    private async Task<IReadOnlyList<NotificationItem>> AlertsAsync(UserContext person) =>
        (await _engine.Notifications.ListAsync(person, unreadOnly: false, limit: 50, before: null)).Where(n => n.Type == "Alert").ToList();

    // ---- events ----

    [Fact]
    public async Task A_new_document_tells_the_people_a_rule_names_but_never_the_person_who_made_it()
    {
        await PeopleAsync();
        await SaveAsync(Rule(message: "{{user}} created {{doctype}} {{name}}: {{title}}", users: [Bob.User, Ann.User]));

        var id = await ClaimAsync(Ann);

        var alert = Assert.Single(await AlertsAsync(Bob));
        Assert.Equal($"ann created Claim {id}: Taxi", alert.Subject);
        Assert.Equal(("Claim", id), (alert.DocType, alert.DocName));
        Assert.Empty(await AlertsAsync(Ann));
    }

    [Fact]
    public async Task An_update_rule_fires_only_when_something_really_changed()
    {
        await PeopleAsync();
        await SaveAsync(Rule(@event: NotificationRule.Update, users: Bob.User));
        var id = await ClaimAsync(Ann);

        await _engine.Documents.UpdateAsync("Claim", id, Values(("title", "Taxi")), Ann); // same value
        Assert.Empty(await AlertsAsync(Bob));

        await _engine.Documents.UpdateAsync("Claim", id, Values(("title", "Train")), Ann);
        Assert.Single(await AlertsAsync(Bob));
    }

    [Fact]
    public async Task A_value_change_rule_watches_one_field_and_can_quote_its_new_value()
    {
        await PeopleAsync();
        await SaveAsync(Rule(@event: NotificationRule.ValueChange, message: "Now {{status}} ({{amount}})", users: Bob.User) with { Field = "status" });
        var id = await ClaimAsync(Ann, ("amount", 12.5m));

        await _engine.Documents.UpdateAsync("Claim", id, Values(("amount", 99)), Ann);
        Assert.Empty(await AlertsAsync(Bob));

        await _engine.Documents.UpdateAsync("Claim", id, Values(("status", "Paid")), Ann);
        Assert.Equal("Now Paid (99)", Assert.Single(await AlertsAsync(Bob)).Subject);
    }

    [Fact]
    public async Task A_condition_limits_a_rule_to_the_documents_it_is_about()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Bob.User) with { Condition = "amount > 1000" });

        await ClaimAsync(Ann, ("amount", 50));
        Assert.Empty(await AlertsAsync(Bob));
        await ClaimAsync(Ann, ("amount", 5000));
        Assert.Single(await AlertsAsync(Bob));
    }

    [Fact]
    public async Task Submitting_and_cancelling_have_their_own_events()
    {
        await PeopleAsync();
        await SaveAsync(
            Rule("On submit", NotificationRule.Submit, "Submitted {{name}}", Bob.User),
            Rule("On cancel", NotificationRule.Cancel, "Cancelled {{name}}", Bob.User));
        var id = await ClaimAsync(Ann);
        Assert.Empty(await AlertsAsync(Bob));

        await _engine.Documents.SubmitAsync("Claim", id, Ann);
        Assert.Equal([$"Submitted {id}"], (await AlertsAsync(Bob)).Select(n => n.Subject));

        await _engine.Documents.CancelAsync("Claim", id, Ann);
        Assert.Equal([$"Cancelled {id}", $"Submitted {id}"], (await AlertsAsync(Bob)).Select(n => n.Subject).Order());
    }

    // ---- who is told ----

    [Fact]
    public async Task Roles_user_fields_the_owner_and_followers_can_all_be_told_once()
    {
        await PeopleAsync();
        await SaveAsync(new NotificationRule
        {
            Name = "Everyone", Event = NotificationRule.Update, Message = "Changed {{name}}", Roles = ["Auditor"], UserFields = ["handler"],
            NotifyOwner = true, NotifyFollowers = true, Users = [Bob.User], // Bob is named three ways: told once
        });
        var id = await ClaimAsync(Ann, ("handler", Cara.User));
        await _engine.Follows.FollowAsync("Claim", id, Bob);

        await _engine.Documents.UpdateAsync("Claim", id, Values(("title", "Changed")), Cara);

        Assert.Single(await AlertsAsync(Bob));
        Assert.Empty(await AlertsAsync(Cara)); // she made the change
        Assert.Single(await AlertsAsync(Ann)); // the owner
        Assert.Empty(await AlertsAsync(Eve));
    }

    [Fact]
    public async Task Someone_who_may_not_read_the_document_is_not_told_about_it()
    {
        await PeopleAsync();
        await SaveAsync(Rule(users: Eve.User));

        await ClaimAsync(Ann);

        Assert.Empty(await AlertsAsync(Eve));
    }

    [Fact]
    public async Task A_message_only_fills_plain_fields_and_cleans_what_it_inserts()
    {
        await PeopleAsync();
        await SaveAsync(Rule(message: "{{ title }} / {{notes}}", users: Bob.User));

        await ClaimAsync(Ann, ("title", "{{name}} and <b>bold</b>"), ("notes", new string('x', 300) + "\nline"));

        var subject = Assert.Single(await AlertsAsync(Bob)).Subject;
        Assert.StartsWith("{{name}} and <b>bold</b> / xxx", subject); // inserted text is never expanded again
        Assert.DoesNotContain('\n', subject);
        Assert.True(subject.Length < 260);
    }

    // ---- days before and after ----

    [Fact]
    public async Task Days_before_a_date_tells_people_once_a_day_from_the_scheduler()
    {
        await PeopleAsync();
        await SaveAsync(Rule("Reminder", NotificationRule.DaysBefore, "{{title}} is due {{due_on}}", Bob.User) with { DateField = "due_on", Days = 3 });
        var today = DateOnly.FromDateTime(_engine.Clock.GetUtcNow().UtcDateTime);
        var soon = await ClaimAsync(Ann, ("title", "Soon"), ("due_on", today.AddDays(3)));
        await ClaimAsync(Ann, ("title", "Later"), ("due_on", today.AddDays(9)));

        Assert.Equal(1, await _engine.NotificationRules.RunScheduledAsync());
        Assert.Equal(0, await _engine.NotificationRules.RunScheduledAsync()); // the same day again: nothing new
        Assert.Equal($"Soon is due {today.AddDays(3):yyyy-MM-dd}", Assert.Single(await AlertsAsync(Bob)).Subject);
        Assert.Equal(soon, Assert.Single(await AlertsAsync(Bob)).DocName);

        _engine.Clock.Advance(TimeSpan.FromDays(6)); // now "Later" is three days away
        Assert.Equal(1, await _engine.NotificationRules.RunScheduledAsync());
        Assert.Equal(2, (await AlertsAsync(Bob)).Count);
    }

    [Fact]
    public async Task Days_after_a_date_and_conditions_work_the_same_way()
    {
        await PeopleAsync();
        await SaveAsync(Rule("Overdue", NotificationRule.DaysAfter, "Overdue: {{title}}", Bob.User) with { DateField = "due_on", Days = 2, Condition = "status != 'Paid'" });
        var today = DateOnly.FromDateTime(_engine.Clock.GetUtcNow().UtcDateTime);
        await ClaimAsync(Ann, ("title", "Unpaid"), ("due_on", today.AddDays(-2)));
        await ClaimAsync(Ann, ("title", "Paid"), ("due_on", today.AddDays(-2)), ("status", "Paid"));

        Assert.Equal(1, await _engine.NotificationRules.RunScheduledAsync());
        Assert.Equal("Overdue: Unpaid", Assert.Single(await AlertsAsync(Bob)).Subject);
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
            Rule("NoOne"),
            Rule("Odd", "Sometimes", users: Bob.User),
            Rule("Watch", NotificationRule.ValueChange, users: Bob.User),
            Rule("Days", NotificationRule.DaysBefore, users: Bob.User) with { DateField = "title", Days = 999 },
            Rule("Msg1", message: "{{ a.b }}", users: Bob.User),
            Rule("Msg2", message: "{{nope}}", users: Bob.User),
            Rule("Msg3", message: "", users: Bob.User),
            Rule("Cond", users: Bob.User) with { Condition = "nope == 1" },
            Rule("Roles", users: Bob.User) with { Roles = ["Ghost Role"], UserFields = ["amount"] }))).Errors.ToDictionary(e => e.Field, e => e.Message);

        Assert.Contains("rules[0].name", errors.Keys);
        Assert.Contains("rules[1].users", errors.Keys);
        Assert.Contains("rules[2].name", errors.Keys);
        Assert.Contains("rules[3].users", errors.Keys);
        Assert.Contains("rules[4].event", errors.Keys);
        Assert.Contains("rules[5].field", errors.Keys);
        Assert.Contains("rules[6].dateField", errors.Keys);
        Assert.Contains("rules[6].days", errors.Keys);
        Assert.Contains("rules[7].message", errors.Keys);
        Assert.Contains("rules[8].message", errors.Keys);
        Assert.Contains("rules[9].message", errors.Keys);
        Assert.Contains("rules[10].condition", errors.Keys);
        Assert.Contains("rules[11].roles", errors.Keys);
        Assert.Contains("rules[11].userFields", errors.Keys);
    }

    [Fact]
    public async Task At_most_twenty_rules()
    {
        await PeopleAsync();
        var many = Enumerable.Range(0, 21).Select(i => Rule($"R{i}", users: Bob.User)).ToArray();
        Assert.Contains("rules", (await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(many))).Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task Only_system_managers_manage_rules_and_a_stale_save_conflicts_and_rules_survive_a_restart()
    {
        await PeopleAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.NotificationRuleManager.ListAsync(Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.NotificationRuleManager.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.NotificationRuleManager.GetAsync("ToDo", TestUsers.SystemManager));

        await SaveAsync(Rule(users: Bob.User));
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(Rule(users: Cara.User)));

        var restarted = new NotificationRuleCatalog();
        var manager = new NotificationRuleManager(_engine.Registry, _engine.NotificationRuleStore, restarted, _engine.Repository, _engine.Permissions, _engine.Clock);
        Assert.Empty(await manager.LoadAsync());
        Assert.Single(restarted.For("Claim")!.Rules);

        await _engine.NotificationRuleManager.DeleteAsync("Claim", TestUsers.SystemManager);
        await ClaimAsync(Ann);
        Assert.Empty(await AlertsAsync(Bob));
    }

    [Fact]
    public async Task The_page_lists_the_fields_a_rule_can_use()
    {
        var view = await _engine.NotificationRuleManager.GetAsync("Claim", TestUsers.SystemManager);
        Assert.Contains("handler", view.UserFields);
        Assert.Contains("amount", view.Fields);
        Assert.Equal(["due_on"], view.DateFields);
    }

    [Fact]
    public void Templates_only_know_plain_names()
    {
        Assert.Equal(["a", "b_1"], MessageTemplate.Names("{{a}} and {{ b_1 }} and {{a}}"));
        Assert.True(MessageTemplate.HasMalformed("{{a.b}}"));
        Assert.True(MessageTemplate.HasMalformed("{{ 1+1 }}"));
        Assert.False(MessageTemplate.HasMalformed("{{a}} {b} }}"));
        Assert.Equal("x-Yes-2026-01-02-", MessageTemplate.Render("{{a}}-{{b}}-{{c}}-{{d}}", n => n switch { "a" => "x", "b" => true, "c" => new DateOnly(2026, 1, 2), _ => null }));
    }
}
