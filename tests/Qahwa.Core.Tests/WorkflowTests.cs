using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class WorkflowTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private static readonly UserContext Manager = new("manager@example.com", ["Approver"]);
    private static readonly UserContext Director = new("director@example.com", ["Director"]);
    private static readonly UserContext Both = new("both@example.com", ["Clerk", "Approver"]); // can create and approve

    public WorkflowTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Expense", "isSubmittable": true, "autoname": "EXP-.####",
              "fields": [
                { "name": "title", "type": "Data" }, { "name": "amount", "type": "Currency" }, { "name": "workflow_state", "type": "Data" }
              ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true },
                { "role": "Approver", "read": true },
                { "role": "Director", "read": true, "write": true }
              ]
            }
            """));
    }

    private static WorkflowDefinition Standard() => new()
    {
        DocType = "Expense",
        StateField = "workflow_state",
        States =
        [
            new() { Name = "Draft", Color = "gray" },
            new() { Name = "Pending Approval", Color = "orange", EditRoles = ["Director"] },
            new() { Name = "Approved", Color = "green", DocStatus = 1 },
            new() { Name = "Rejected", Color = "red" },
            new() { Name = "Cancelled", Color = "purple", DocStatus = 2 },
        ],
        Transitions =
        [
            new() { From = "Draft", Action = "Submit for approval", To = "Pending Approval", Roles = ["Clerk"] },
            new() { From = "Pending Approval", Action = "Approve", To = "Approved", Roles = ["Approver"], Condition = "amount <= 1000", AllowSelfApproval = false },
            new() { From = "Pending Approval", Action = "Director approve", To = "Approved", Roles = ["Director"], Condition = "amount > 1000" },
            new() { From = "Pending Approval", Action = "Reject", To = "Rejected", Roles = ["Approver", "Director"] },
            new() { From = "Rejected", Action = "Reopen", To = "Draft", Roles = ["Clerk"] },
            new() { From = "Approved", Action = "Cancel", To = "Cancelled", Roles = ["Director"] },
        ],
    };

    // ---- several people must approve ----

    private static readonly UserContext Manager2 = new("manager2@example.com", ["Approver"]);

    private static WorkflowDefinition TwoApprovals() => Standard() with
    {
        Transitions = Standard().Transitions.Select(t => t.Action == "Approve" ? t with { RequiredApprovals = 2 } : t).ToList(),
    };

    private async Task<string> PendingExpenseAsync()
    {
        await SaveAsync(TwoApprovals());
        var id = await ExpenseAsync(Clerk);
        await DoAsync(id, "Submit for approval", Clerk);
        return id;
    }

    [Fact]
    public async Task A_step_that_needs_two_people_waits_for_the_second_and_each_person_counts_once()
    {
        var id = await PendingExpenseAsync();

        var before = (await _engine.Documents.WorkflowStatusAsync("Expense", id, Manager))!.Actions.Single(a => a.Action == "Approve");
        Assert.Equal((2, 0), (before.Required, before.Given));

        await DoAsync(id, "Approve", Manager);
        Assert.Equal("Pending Approval", await StateAsync(id));
        Assert.DoesNotContain("Approve", await ActionsAsync(id, Manager)); // already approved
        await Assert.ThrowsAsync<PermissionDeniedException>(() => DoAsync(id, "Approve", Manager));

        var waiting = (await _engine.Documents.WorkflowStatusAsync("Expense", id, Manager2))!.Actions.Single(a => a.Action == "Approve");
        Assert.Equal((2, 1), (waiting.Required, waiting.Given));

        await DoAsync(id, "Approve", Manager2);
        Assert.Equal("Approved", await StateAsync(id));
    }

    [Fact]
    public async Task Moving_on_by_another_action_starts_the_next_round_with_nobody_having_approved()
    {
        var id = await PendingExpenseAsync();
        await DoAsync(id, "Approve", Manager);
        await DoAsync(id, "Reject", Manager2);
        await DoAsync(id, "Reopen", Clerk);
        await DoAsync(id, "Submit for approval", Clerk);

        var again = (await _engine.Documents.WorkflowStatusAsync("Expense", id, Manager))!.Actions.Single(a => a.Action == "Approve");
        Assert.Equal(0, again.Given);
    }

    [Fact]
    public async Task The_number_of_people_must_be_between_one_and_ten()
    {
        foreach (var bad in new[] { 0, 11 })
        {
            var definition = Standard() with { Transitions = Standard().Transitions.Select(t => t.Action == "Approve" ? t with { RequiredApprovals = bad } : t).ToList() };
            var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(definition));
            Assert.Contains(ex.Errors, e => e.Field.EndsWith("requiredApprovals"));
        }
    }

    // ---- escalation ----

    private static WorkflowDefinition Escalating(int hours, string? action, params string[] notify) => Standard() with
    {
        States = Standard().States.Select(st => st.Name == "Pending Approval" ? st with { EscalateAfterHours = hours, EscalateAction = action, EscalateNotify = notify } : st).ToList(),
    };

    private async Task DirectorUserAsync()
    {
        if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), "Director"))
            await _engine.Documents.InsertAsync("Role", Values(("role_name", "Director")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", Director.User), ("full_name", "Dee Rector"), ("enabled", true)), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", Director.User), ("role", "Director")), TestUsers.Admin);
    }

    private async Task<string> SubmittedExpenseAsync()
    {
        var id = await ExpenseAsync(Clerk);
        await DoAsync(id, "Submit for approval", Clerk);
        return id;
    }

    [Fact]
    public async Task An_overdue_document_tells_the_roles_once_and_stays_where_it_is()
    {
        await DirectorUserAsync();
        await SaveAsync(Escalating(24, null, "Director"));
        var id = await SubmittedExpenseAsync();

        _engine.Clock.Advance(TimeSpan.FromHours(23));
        Assert.Equal((0, 0), await _engine.WorkflowEscalations.RunDueAsync());

        _engine.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal((1, 0), await _engine.WorkflowEscalations.RunDueAsync());
        Assert.Equal((0, 0), await _engine.WorkflowEscalations.RunDueAsync()); // once per wait

        var inbox = await _engine.Notifications.ListAsync(Director, unreadOnly: false, limit: 50, before: null);
        Assert.Contains(inbox, n => n.Subject.Contains("has been \"Pending Approval\" for 24 hour(s)"));
        Assert.Equal("Pending Approval", await StateAsync(id));
    }

    [Fact]
    public async Task An_overdue_document_can_be_moved_on_by_the_system_and_a_later_wait_escalates_again()
    {
        await SaveAsync(Escalating(48, "Reject"));
        var id = await SubmittedExpenseAsync();

        _engine.Clock.Advance(TimeSpan.FromHours(49));
        Assert.Equal((1, 0), await _engine.WorkflowEscalations.RunDueAsync());
        Assert.Equal("Rejected", await StateAsync(id));

        await DoAsync(id, "Reopen", Clerk);
        await DoAsync(id, "Submit for approval", Clerk);
        _engine.Clock.Advance(TimeSpan.FromHours(49));
        Assert.Equal((1, 0), await _engine.WorkflowEscalations.RunDueAsync());
        Assert.Equal("Rejected", await StateAsync(id));
    }

    [Fact]
    public async Task Escalation_settings_are_checked()
    {
        async Task<DocumentValidationException> Fails(WorkflowDefinition d) => await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(d));

        Assert.Contains((await Fails(Escalating(24, null))).Errors, e => e.Field == "states[1].escalateAction"); // nothing to do
        Assert.Contains((await Fails(Escalating(24, "Nope"))).Errors, e => e.Message.Contains("not an action that leaves"));
        Assert.Contains((await Fails(Escalating(99999, null, "Director"))).Errors, e => e.Field == "states[1].escalateAfterHours");

        var several = Escalating(24, "Approve");
        several = several with { Transitions = several.Transitions.Select(t => t.Action == "Approve" ? t with { RequiredApprovals = 2 } : t).ToList() };
        Assert.Contains((await Fails(several)).Errors, e => e.Message.Contains("several people"));
    }

    // ---- approve or reject from an email ----

    private static WorkflowDefinition Emailing() => Standard() with
    {
        States = Standard().States.Select(st => st.Name == "Pending Approval" ? st with { EmailApprovers = true } : st).ToList(),
    };

    private async Task UsersAsync()
    {
        foreach (var (person, role) in new[] { (Manager, "Approver"), (Director, "Director"), (Clerk, "Clerk") })
        {
            if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), role))
                await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
            await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
        }
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> MailAsync() =>
        await _engine.Repository.ListAsync(_engine.Registry.Get("Email Queue"), new ListQuery { OrderBy = "creation", Descending = false, Limit = 100 });

    private static string[] TokensIn(string body) =>
        System.Text.RegularExpressions.Regex.Matches(body, @"https://qahwa\.example/workflow-action/([\w-]+)").Select(m => m.Groups[1].Value).ToArray();

    private async Task<(string Id, string Token)> ApproveLinkAsync()
    {
        await UsersAsync();
        _engine.AuthOptions.PublicUrl = "https://qahwa.example";
        await SaveAsync(Emailing());
        var id = await SubmittedExpenseAsync();
        var mail = (await MailAsync()).Single(m => (string)m["recipient"]! == Manager.User);
        var body = (string)mail["body"]!;
        var line = body.Split('\n').Single(l => l.StartsWith("Approve:"));
        return (id, TokensIn(line).Single());
    }

    [Fact]
    public async Task Entering_a_state_emails_each_approver_one_link_per_action_they_can_take()
    {
        await UsersAsync();
        _engine.AuthOptions.PublicUrl = "https://qahwa.example";
        await SaveAsync(Emailing());
        await SubmittedExpenseAsync();

        var mail = await MailAsync();
        var manager = mail.Single(m => (string)m["recipient"]! == Manager.User);
        var director = mail.Single(m => (string)m["recipient"]! == Director.User);
        Assert.Equal(2, TokensIn((string)manager["body"]!).Length); // Approve and Reject
        Assert.Single(TokensIn((string)director["body"]!)); // the big-amount approval does not apply to a small one
        Assert.DoesNotContain(mail, m => (string)m["recipient"]! == Clerk.User);

        // Only a hash is kept.
        var token = TokensIn((string)manager["body"]!)[0];
        var stored = await _engine.Repository.ListAsync(_engine.Registry.Get("Workflow Link"), new ListQuery { Limit = 50 });
        Assert.DoesNotContain(stored, r => (string)r["token_hash"]! == token);
    }

    [Fact]
    public async Task Without_a_site_address_no_links_are_sent()
    {
        await UsersAsync();
        await SaveAsync(Emailing());
        await SubmittedExpenseAsync();
        Assert.Empty(await MailAsync());
    }

    [Fact]
    public async Task A_link_only_works_for_its_person_once_and_asks_for_confirmation()
    {
        var (id, token) = await ApproveLinkAsync();

        var info = await _engine.WorkflowLinks.InfoAsync(token, Manager); // looking changes nothing
        Assert.Equal(("Expense", id, "Approve", "Approved"), (info.DocType, info.Name, info.Action, info.To));
        Assert.Equal("Pending Approval", await StateAsync(id));

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WorkflowLinks.InfoAsync(token, Director)); // someone else's link
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.WorkflowLinks.InfoAsync(token, UserContext.Anonymous));

        await _engine.WorkflowLinks.ConfirmAsync(token, Manager);
        Assert.Equal("Approved", await StateAsync(id));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WorkflowLinks.ConfirmAsync(token, Manager)); // used
    }

    [Fact]
    public async Task A_link_dies_when_it_expires_or_the_document_moves_on()
    {
        var (_, expiring) = await ApproveLinkAsync();
        _engine.Clock.Advance(TimeSpan.FromDays(8));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WorkflowLinks.InfoAsync(expiring, Manager));
    }

    [Fact]
    public async Task Another_action_on_the_document_voids_the_other_links()
    {
        var (id, token) = await ApproveLinkAsync();
        await DoAsync(id, "Reject", Director);
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WorkflowLinks.ConfirmAsync(token, Manager));
        Assert.Equal("Rejected", await StateAsync(id));
    }

    // ---- creating the state field from the builder ----

    [Fact]
    public async Task A_state_field_is_added_to_a_designer_made_DocType_and_can_then_be_used_by_a_workflow()
    {
        await _engine.Designer.CreateAsync(DocTypeLoader.Parse("""{ "name": "Leave", "fields": [ { "name": "reason", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""), TestUsers.SystemManager);

        var field = await _engine.WorkflowStateFields.EnsureAsync("Leave", TestUsers.SystemManager);

        Assert.Equal("workflow_state", field);
        Assert.Contains("workflow_state", (await _engine.Workflows.GetAsync("Leave", TestUsers.SystemManager)).StateFields);
        var definition = new WorkflowDefinition { DocType = "Leave", StateField = field, Active = true, States = [new() { Name = "Open" }, new() { Name = "Done" }], Transitions = [new() { From = "Open", Action = "Finish", To = "Done", Roles = ["Clerk"] }] };
        await _engine.Workflows.SaveAsync("Leave", definition, null, TestUsers.SystemManager);
        var id = (string)(await _engine.Documents.InsertAsync("Leave", Values(("reason", "Holiday")), Clerk))["name"]!;
        Assert.Equal("Open", (await _engine.Documents.GetAsync("Leave", id, Clerk))["workflow_state"]);
    }

    [Fact]
    public async Task A_state_field_on_a_DocType_with_its_own_definition_is_a_custom_field_and_asking_again_reuses_it()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Ticket", "fields": [ { "name": "title", "type": "Data" } ] }"""));

        var first = await _engine.WorkflowStateFields.EnsureAsync("Ticket", TestUsers.Admin);
        var second = await _engine.WorkflowStateFields.EnsureAsync("Ticket", TestUsers.Admin);

        Assert.Equal("custom_workflow_state", first);
        Assert.Equal(first, second);
        Assert.Single(_engine.Registry.Get("Ticket").Fields, f => f.Name == first);
    }

    [Fact]
    public async Task Only_a_manager_can_add_a_state_field_and_a_name_clash_is_refused()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.WorkflowStateFields.EnsureAsync("Expense", Clerk));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Odd", "fields": [ { "name": "custom_workflow_state", "type": "Int" } ] }"""));
        await Assert.ThrowsAsync<ConflictException>(() => _engine.WorkflowStateFields.EnsureAsync("Odd", TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WorkflowStateFields.EnsureAsync("Nope", TestUsers.Admin));
    }

    private Task<WorkflowView> SaveAsync(WorkflowDefinition definition, DateTime? expected = null, IUserContext? user = null) =>
        _engine.Workflows.SaveAsync("Expense", definition, expected, user ?? TestUsers.SystemManager);

    private async Task<string> ExpenseAsync(UserContext by, decimal amount = 100, params (string, object?)[] extra)
    {
        var values = Values([("title", "Taxi"), ("amount", amount), .. extra]);
        return (string)(await _engine.Documents.InsertAsync("Expense", values, by))["name"]!;
    }

    private async Task<IReadOnlyList<string>> ActionsAsync(string id, IUserContext user) =>
        (await _engine.Documents.WorkflowStatusAsync("Expense", id, user))!.Actions.Select(a => a.Action).ToList();

    private Task DoAsync(string id, string action, IUserContext user) =>
        _engine.Documents.ApplyWorkflowActionAsync("Expense", id, action, null, user);

    private async Task<string> StateAsync(string id) =>
        (string)(await _engine.Documents.GetAsync("Expense", id, TestUsers.Admin))["workflow_state"]!;

    // ---- using a workflow ----

    [Fact]
    public async Task A_new_document_starts_in_the_first_state_whatever_was_sent()
    {
        await SaveAsync(Standard());

        var id = await ExpenseAsync(Clerk, 100, ("workflow_state", "Approved"));

        Assert.Equal("Draft", await StateAsync(id));
        var status = (await _engine.Documents.WorkflowStatusAsync("Expense", id, Clerk))!;
        Assert.Equal(("workflow_state", "Draft", "gray"), (status.StateField, status.State, status.Color));
    }

    [Fact]
    public async Task Without_a_workflow_nothing_changes()
    {
        var id = await ExpenseAsync(Clerk, 100, ("workflow_state", "Anything"));

        Assert.Equal("Anything", await StateAsync(id));
        Assert.Null(await _engine.Documents.WorkflowStatusAsync("Expense", id, Clerk));
        await _engine.Documents.SubmitAsync("Expense", id, TestUsers.Admin);
    }

    [Fact]
    public async Task Only_the_workflow_changes_the_state_field()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Expense", id, Values(("workflow_state", "Approved")), Clerk));
        Assert.Contains(ex.Errors, e => e.Field == "workflow_state");
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Expense", id, Values(("workflow_state", "Approved")), TestUsers.Admin));
        await _engine.Documents.UpdateAsync("Expense", id, Values(("workflow_state", "Draft"), ("amount", 5)), Clerk); // echoing the state is fine
    }

    [Fact]
    public async Task Each_person_is_offered_only_the_actions_their_role_and_the_conditions_allow()
    {
        await SaveAsync(Standard());
        var small = await ExpenseAsync(Clerk, 500);
        var large = await ExpenseAsync(Clerk, 5000);

        Assert.Equal(["Submit for approval"], await ActionsAsync(small, Clerk));
        Assert.Empty(await ActionsAsync(small, Manager));
        await DoAsync(small, "Submit for approval", Clerk);
        await DoAsync(large, "Submit for approval", Clerk);

        Assert.Equal(["Approve", "Reject"], await ActionsAsync(small, Manager));
        Assert.Equal(["Reject"], await ActionsAsync(large, Manager));             // over the limit
        Assert.Equal(["Director approve", "Reject"], await ActionsAsync(large, Director));
        Assert.Equal(["Reject"], await ActionsAsync(small, Director));
        Assert.Empty(await ActionsAsync(small, Clerk));
    }

    [Fact]
    public async Task Self_approval_can_be_forbidden_per_step()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Both, 100);
        await DoAsync(id, "Submit for approval", Both);

        Assert.Equal(["Reject"], await ActionsAsync(id, Both)); // she wrote it, so she cannot Approve it, but may Reject
        await Assert.ThrowsAsync<PermissionDeniedException>(() => DoAsync(id, "Approve", Both));
    }

    [Fact]
    public async Task An_action_needs_only_read_access_and_a_role_of_the_step()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);
        await DoAsync(id, "Submit for approval", Clerk);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.UpdateAsync("Expense", id, Values(("amount", 1)), Manager)); // no write

        await DoAsync(id, "Approve", Manager);

        Assert.Equal("Approved", await StateAsync(id));
    }

    [Fact]
    public async Task A_state_can_submit_and_cancel_the_document()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);
        await DoAsync(id, "Submit for approval", Clerk);
        Assert.Equal(0, (await _engine.Documents.GetAsync("Expense", id, Clerk))["docstatus"]);

        await DoAsync(id, "Approve", Manager);
        Assert.Equal(1, (await _engine.Documents.GetAsync("Expense", id, Clerk))["docstatus"]);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Expense", id, Values(("title", "changed")), Clerk)); // locked

        await DoAsync(id, "Cancel", Director);
        Assert.Equal(2, (await _engine.Documents.GetAsync("Expense", id, Clerk))["docstatus"]);
        Assert.Equal("Cancelled", await StateAsync(id));
        Assert.Empty(await ActionsAsync(id, Director));
    }

    [Fact]
    public async Task With_a_workflow_submit_and_cancel_are_only_done_through_it()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Expense", id, TestUsers.Admin));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.CancelAsync("Expense", id, TestUsers.Admin));
    }

    [Fact]
    public async Task Who_may_edit_depends_on_the_state()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);
        await _engine.Documents.UpdateAsync("Expense", id, Values(("amount", 120)), Clerk); // Draft: anyone with write
        await DoAsync(id, "Submit for approval", Clerk);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Expense", id, Values(("amount", 130)), Clerk));
        Assert.Contains("Director", ex.Errors[0].Message);
        await _engine.Documents.UpdateAsync("Expense", id, Values(("amount", 130)), Director); // the state's editor

        Assert.False((await _engine.Documents.WorkflowStatusAsync("Expense", id, Clerk))!.CanEdit);
        Assert.True((await _engine.Documents.WorkflowStatusAsync("Expense", id, Director))!.CanEdit);
    }

    [Fact]
    public async Task Actions_are_logged_and_followers_are_told()
    {
        await _engine.Documents.InsertAsync("User", Values(("email", Clerk.User), ("full_name", "Carla Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", Manager.User), ("full_name", "Mo Manager")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Role", Values(("role_name", "Clerk")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("Has Role", Values(("user", Clerk.User), ("role", "Clerk")), TestUsers.Admin);
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        await DoAsync(id, "Submit for approval", Clerk);
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await DoAsync(id, "Reject", Manager);

        var timeline = await _engine.Activity.TimelineAsync("Expense", id, Clerk);
        var change = timeline.First(i => i.Event == "Update").Data!["changed"]![0]!;
        Assert.Equal(["workflow_state", "Pending Approval", "Rejected"], change.AsArray().Select(x => x!.ToString()).ToArray());
        Assert.Contains((await _engine.Notifications.ListAsync(Clerk, false, 10, null)), n => n.Subject.Contains("changed"));
    }

    [Fact]
    public async Task A_missing_unavailable_or_unknown_action_is_refused()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => DoAsync(id, "Approve", Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => DoAsync(id, "", Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ApplyWorkflowActionAsync("Expense", id, null, null, Clerk));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => DoAsync(id, "Submit for approval", TestUsers.Nobody)); // cannot even read it
        await Assert.ThrowsAsync<NotFoundException>(() => DoAsync("MISSING", "Submit for approval", Clerk));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ApplyWorkflowActionAsync("Territory", "x", "Go", null, Clerk));
        await DoAsync(id, "submit FOR approval", Clerk); // the action's case does not matter
    }

    [Fact]
    public async Task A_stale_modified_value_stops_an_action()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);
        var seen = (DateTime)(await _engine.Documents.GetAsync("Expense", id, Clerk))["modified"]!;
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Expense", id, Values(("amount", 7)), Clerk);

        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.ApplyWorkflowActionAsync("Expense", id, "Submit for approval", seen, Clerk));
    }

    [Fact]
    public async Task The_inbox_lists_what_is_waiting_for_each_person()
    {
        await SaveAsync(Standard());
        var small = await ExpenseAsync(Clerk, 100);
        var large = await ExpenseAsync(Clerk, 9000);
        var draft = await ExpenseAsync(Clerk, 50);
        await DoAsync(small, "Submit for approval", Clerk);
        await DoAsync(large, "Submit for approval", Clerk);

        var mine = await _engine.Documents.PendingWorkflowAsync(Manager);
        var directors = await _engine.Documents.PendingWorkflowAsync(Director);
        var clerks = await _engine.Documents.PendingWorkflowAsync(Clerk);

        Assert.Equal([small, large], mine.Select(p => p.Name).Order()); // Reject is open to the Approver for both
        Assert.Equal(["Approve", "Reject"], mine.Single(p => p.Name == small).Actions.Select(a => a.Action));
        Assert.Equal(["Reject"], mine.Single(p => p.Name == large).Actions.Select(a => a.Action));
        Assert.Equal(["Director approve", "Reject"], directors.Single(p => p.Name == large).Actions.Select(a => a.Action));
        Assert.Equal([draft], clerks.Select(p => p.Name));
        Assert.Equal("Pending Approval", mine[0].State);
        Assert.Empty(await _engine.Documents.PendingWorkflowAsync(TestUsers.Nobody));
        Assert.Empty(await _engine.Documents.PendingWorkflowAsync(UserContext.Anonymous));
    }

    [Fact]
    public async Task A_document_with_no_state_counts_as_being_in_the_first_state()
    {
        var id = await ExpenseAsync(Clerk, 100); // created before the workflow existed
        await SaveAsync(Standard());

        Assert.Equal(["Submit for approval"], await ActionsAsync(id, Clerk));
        await DoAsync(id, "Submit for approval", Clerk);
        Assert.Equal("Pending Approval", await StateAsync(id));
    }

    [Fact]
    public async Task Deactivating_or_deleting_a_workflow_frees_the_document()
    {
        await SaveAsync(Standard());
        var id = await ExpenseAsync(Clerk, 100);

        await SaveAsync(Standard() with { Active = false }, _engine.WorkflowStore.Modified("Expense"));
        await _engine.Documents.UpdateAsync("Expense", id, Values(("workflow_state", "Whatever")), Clerk);
        Assert.Null(await _engine.Documents.WorkflowStatusAsync("Expense", id, Clerk));

        await SaveAsync(Standard(), _engine.WorkflowStore.Modified("Expense"));
        await _engine.Workflows.DeleteAsync("Expense", TestUsers.SystemManager);
        await _engine.Documents.UpdateAsync("Expense", id, Values(("workflow_state", "Free")), Clerk);
    }

    // ---- building a workflow ----

    [Fact]
    public async Task A_workflow_is_checked_and_the_errors_say_where()
    {
        var bad = new WorkflowDefinition
        {
            DocType = "Expense",
            StateField = "amount",
            States =
            [
                new() { Name = "Draft", DocStatus = 1 },
                new() { Name = "draft", Color = "pink" },
                new() { Name = "Done", DocStatus = 0 },
                new() { Name = "Late", DocStatus = 9 },
                new() { Name = "Bad<name>" },
            ],
            Transitions =
            [
                new() { From = "Nope", Action = "Go", To = "Done", Roles = ["R"] },
                new() { From = "Done", Action = "", To = "Ghost", Roles = [] },
                new() { From = "Done", Action = "Back", To = "Draft", Roles = ["R"] },
                new() { From = "Done", Action = "back", To = "Draft", Roles = ["R"] },
                new() { From = "Done", Action = "Cond", To = "Done", Roles = ["R"], Condition = "unknown_field > 1" },
                new() { From = "Done", Action = "Syntax", To = "Done", Roles = ["R"], Condition = "amount >" },
            ],
        };

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(bad));

        var paths = ex.Errors.Select(e => e.Field).ToHashSet();
        foreach (var expected in new[]
        {
            "stateField", "states[0].docStatus", "states[1].name", "states[1].color", "states[3].docStatus", "states[4].name",
            "transitions[0].from", "transitions[1].action", "transitions[1].to", "transitions[1].roles", "transitions[3].action",
            "transitions[4].condition", "transitions[5].condition",
        })
            Assert.Contains(expected, paths);
        Assert.Null(_engine.WorkflowCatalog.For("Expense")); // nothing took effect
    }

    [Fact]
    public async Task Documents_can_only_move_forward_through_submit_and_cancel()
    {
        var wf = Standard() with
        {
            Transitions = [new() { From = "Approved", Action = "Undo", To = "Draft", Roles = ["Director"] }, new() { From = "Cancelled", Action = "Revive", To = "Approved", Roles = ["Director"] }],
        };

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => SaveAsync(wf));

        Assert.Contains(ex.Errors, e => e.Field == "transitions[0].to");
        Assert.Contains(ex.Errors, e => e.Field == "transitions[1].to");
    }

    [Fact]
    public async Task Only_a_submittable_doctype_can_use_document_status_and_a_select_state_field_needs_the_states_as_choices()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo Item", "fields": [ { "name": "text", "type": "Data" }, { "name": "stage", "type": "Select", "options": "New\nDone" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Workflows.SaveAsync("Memo Item", new WorkflowDefinition
        {
            DocType = "Memo Item", StateField = "stage",
            States = [new() { Name = "New" }, new() { Name = "Done", DocStatus = 1 }, new() { Name = "Archived" }],
        }, null, TestUsers.SystemManager));

        Assert.Contains(ex.Errors, e => e.Field == "states[1].docStatus");
        Assert.Contains(ex.Errors, e => e.Field == "states[2].name" && e.Message.Contains("choices"));
        await _engine.Workflows.SaveAsync("Memo Item", new WorkflowDefinition
        {
            DocType = "Memo Item", StateField = "stage",
            States = [new() { Name = "New" }, new() { Name = "Done" }],
            Transitions = [new() { From = "New", Action = "Finish", To = "Done", Roles = ["Clerk"] }],
        }, null, TestUsers.SystemManager);
        var note = (string)(await _engine.Documents.InsertAsync("Memo Item", Values(("text", "x")), Clerk))["name"]!;
        await _engine.Documents.ApplyWorkflowActionAsync("Memo Item", note, "Finish", null, Clerk);
        Assert.Equal("Done", (await _engine.Documents.GetAsync("Memo Item", note, Clerk))["stage"]);
    }

    [Fact]
    public async Task Only_system_managers_build_workflows_and_only_administrators_touch_framework_doctypes()
    {
        await Assert.ThrowsAsync<PermissionDeniedException>(() => SaveAsync(Standard(), user: Manager));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Workflows.ListAsync(UserContext.Anonymous));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workflows.DeleteAsync("Expense", Clerk));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Line", "isChildTable": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Workflows.GetAsync("Line", TestUsers.Admin));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Workflows.GetAsync("Nope", TestUsers.Admin));

        var core = new WorkflowDefinition { DocType = "Role", StateField = "role_name", States = [new() { Name = "A" }] };
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Workflows.SaveAsync("Role", core, null, TestUsers.SystemManager));
        await _engine.Workflows.SaveAsync("Role", core, null, TestUsers.Admin);
    }

    [Fact]
    public async Task The_list_and_the_view_show_what_is_configured()
    {
        await SaveAsync(Standard());

        var items = await _engine.Workflows.ListAsync(TestUsers.SystemManager);
        var expense = items.Single(i => i.DocType == "Expense");
        var view = await _engine.Workflows.GetAsync("Expense", TestUsers.SystemManager);

        Assert.True(expense.HasWorkflow);
        Assert.True(expense.Active);
        Assert.Equal(5, expense.States);
        Assert.False(items.Single(i => i.DocType == "Customer").HasWorkflow);
        Assert.Contains("workflow_state", view.StateFields);
        Assert.Equal(6, view.Workflow.Transitions.Count);
        Assert.True(view.CanEdit);
        Assert.False((await _engine.Workflows.GetAsync("Role", TestUsers.SystemManager)).CanEdit);
    }

    [Fact]
    public async Task Concurrent_edits_are_detected_and_workflows_survive_a_restart()
    {
        var first = await SaveAsync(Standard());
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(Standard())); // someone who did not see the first

        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        var second = await SaveAsync(Standard(), first.Modified);
        Assert.True(second.Modified > first.Modified);
        await Assert.ThrowsAsync<ConflictException>(() => SaveAsync(Standard(), first.Modified));

        await _engine.WorkflowStore.SaveAsync(new StoredWorkflow("Territory", "{ nope", DateTime.UtcNow, "x"), null);
        var restarted = new WorkflowCatalog();
        var failures = await new WorkflowManager(_engine.Registry, _engine.WorkflowStore, restarted, _engine.Permissions, _engine.Clock).LoadAsync();

        Assert.Single(failures);
        Assert.NotNull(restarted.For("Expense"));
        Assert.Null(restarted.For("Territory"));
    }
}
