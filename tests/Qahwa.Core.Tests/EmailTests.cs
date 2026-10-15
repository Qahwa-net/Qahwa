using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class EmailTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Viewer = new("vera@example.com", ["Viewer"]);

    public EmailTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quote", "fields": [ { "name": "customer", "type": "Data" }, { "name": "total", "type": "Currency" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Viewer", "read": true } ] }
            """));
    }

    private async Task UserAsync(UserContext person)
    {
        foreach (var role in person.Roles)
        {
            if (!await _engine.Repository.ExistsAsync(_engine.Registry.Get("Role"), role))
                await _engine.Documents.InsertAsync("Role", Values(("role_name", role)), TestUsers.Admin);
        }

        await _engine.Documents.InsertAsync("User", Values(("email", person.User), ("full_name", person.User), ("enabled", true)), TestUsers.Admin);
        foreach (var role in person.Roles)
            await _engine.Documents.InsertAsync("Has Role", Values(("user", person.User), ("role", role)), TestUsers.Admin);
    }

    private async Task<string> QuoteAsync(UserContext by) =>
        (string)(await _engine.Documents.InsertAsync("Quote", Values(("customer", "Acme"), ("total", 120)), by))["name"]!;

    private Task TemplateAsync(string name = "Quote ready", string? doctype = null, string subject = "Your quote {{name}}", string body = "Hello {{customer}}, the total is {{total}}. From {{user}}.") =>
        _engine.Documents.InsertAsync("Email Template", Values(("template_name", name), ("reference_doctype", doctype), ("subject", subject), ("body", body)), TestUsers.Admin);

    private async Task<List<Dictionary<string, object?>>> QueueAsync() =>
        (await _engine.Documents.ListAsync("Email Queue", new ListRequest { Fields = ["recipient", "subject", "body", "status", "attempts", "last_error"], OrderBy = "creation", Descending = false, Limit = 100 }, TestUsers.Admin)).Data.ToList();

    // ---- addresses ----

    [Theory]
    [InlineData("a@example.com", true)]
    [InlineData("first.last+tag@sub.example.co.uk", true)]
    [InlineData("a@example", false)]
    [InlineData("Ann <a@example.com>", false)]
    [InlineData("a@example.com, b@example.com", false)]
    [InlineData("a@example.com\r\nBcc: evil@example.com", false)]
    [InlineData("a b@example.com", false)]
    [InlineData("<a@example.com>", false)]
    [InlineData("", false)]
    public void Only_plain_addresses_are_accepted(string address, bool valid) => Assert.Equal(valid, EmailService.ValidAddress(address));

    // ---- queueing and sending ----

    [Fact]
    public async Task Emailing_from_a_document_queues_one_message_per_address_and_notes_it_in_the_thread()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);

        var queued = await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com", "y@example.com", "X@example.com"], "Hi\r\nBcc: evil@example.com", "Body"), Ann);

        Assert.Equal(2, queued);
        var queue = await QueueAsync();
        Assert.Equal(["x@example.com", "y@example.com"], queue.Select(r => (string)r["recipient"]!).Order());
        Assert.All(queue, r => Assert.Equal(("Pending", "Hi  Bcc: evil@example.com"), ((string)r["status"]!, (string)r["subject"]!))); // the line break cannot become a header
        Assert.Contains(await _engine.Activity.TimelineAsync("Quote", id, Ann), i => i.Content == "Emailed x@example.com, y@example.com");
    }

    [Fact]
    public async Task The_scheduler_sends_what_is_queued_once()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "Hello", "Text"), Ann);

        Assert.Equal(new EmailRun(1, 0), await _engine.Email.SendPendingAsync());
        Assert.Equal(new EmailRun(0, 0), await _engine.Email.SendPendingAsync());

        var sent = Assert.Single(_engine.EmailSender.Sent);
        Assert.Equal(("x@example.com", "Hello", "Text"), (sent.To, sent.Subject, sent.Body));
        Assert.Equal("Sent", (string)Assert.Single(await QueueAsync())["status"]!);
    }

    [Fact]
    public async Task A_failed_send_is_retried_and_given_up_after_three_attempts()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "Hello", "Text"), Ann);
        _engine.EmailSender.Fail(5);

        for (var i = 0; i < 4; i++)
        {
            _engine.Clock.Advance(TimeSpan.FromMinutes(1));
            await _engine.Email.SendPendingAsync();
        }

        var row = Assert.Single(await QueueAsync());
        Assert.Equal(("Failed", 3L, "The mail server said no."), ((string)row["status"]!, Convert.ToInt64(row["attempts"]), (string)row["last_error"]!));
        Assert.Empty(_engine.EmailSender.Sent);
    }

    [Fact]
    public async Task A_message_that_failed_once_goes_out_on_the_next_try()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "Hello", "Text"), Ann);
        _engine.EmailSender.Fail(1);

        Assert.Equal(new EmailRun(0, 1), await _engine.Email.SendPendingAsync());
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(new EmailRun(1, 0), await _engine.Email.SendPendingAsync());
        Assert.Equal("Sent", (string)Assert.Single(await QueueAsync())["status"]!);
    }

    [Fact]
    public async Task Several_schedulers_at_once_send_each_message_once()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com", "y@example.com"], "Hello", "Text"), Ann);

        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _engine.Email.SendPendingAsync()));

        Assert.Equal(2, runs.Sum(r => r.Sent));
        Assert.Equal(2, _engine.EmailSender.Sent.Count);
    }

    [Fact]
    public async Task A_message_stuck_in_sending_from_a_dead_server_is_picked_up_after_ten_minutes()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "Hello", "Text"), Ann);
        var row = Assert.Single(await QueueAsync());
        var queue = _engine.Registry.Get("Email Queue");
        var stored = await _engine.Repository.GetAsync(queue, (await _engine.Documents.ListAsync("Email Queue", new ListRequest { Fields = ["name"], Limit = 1 }, TestUsers.Admin)).Data[0]["name"]!.ToString()!);
        await _engine.Repository.UpdateAsync(queue, (string)stored!["name"]!, new Dictionary<string, object?> { ["status"] = "Sending", ["attempts"] = 1L }, null);

        Assert.Equal(new EmailRun(0, 0), await _engine.Email.SendPendingAsync()); // someone else may be sending it
        _engine.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(new EmailRun(1, 0), await _engine.Email.SendPendingAsync());
        Assert.NotNull(row);
    }

    // ---- templates ----

    [Fact]
    public async Task A_template_fills_in_the_document_and_the_sender()
    {
        await UserAsync(Ann);
        await TemplateAsync(doctype: "Quote");
        var id = await QuoteAsync(Ann);

        var preview = await _engine.Email.PreviewAsync("Quote", id, "Quote ready", Ann);

        Assert.Equal(($"Your quote {id}", "Hello Acme, the total is 120. From ann@example.com."), (preview.Subject, preview.Message));
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], Template: "Quote ready"), Ann);
        Assert.Equal(preview.Subject, (string)Assert.Single(await QueueAsync())["subject"]!);
    }

    [Fact]
    public async Task Templates_for_another_doctype_are_refused_and_listed_only_where_they_fit()
    {
        await UserAsync(Ann);
        await TemplateAsync("For quotes", "Quote");
        await TemplateAsync("For anything");
        await TemplateAsync("For users", "User");
        var id = await QuoteAsync(Ann);

        Assert.Equal(["For anything", "For quotes"], (await _engine.Email.TemplatesAsync("Quote", Ann)).Select(t => t.Name).Order());
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Email.PreviewAsync("Quote", id, "For users", Ann));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Email.PreviewAsync("Quote", id, "Missing", Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Email.TemplatesAsync("Quote", UserContext.Anonymous));
    }

    [Fact]
    public async Task A_template_cannot_reach_beyond_the_document_and_values_are_not_expanded_again()
    {
        await UserAsync(Ann);
        await TemplateAsync(body: "{{customer}} / {{password}} / {{nope}}");
        var id = (string)(await _engine.Documents.InsertAsync("Quote", Values(("customer", "{{user}}")), Ann))["name"]!;

        var preview = await _engine.Email.PreviewAsync("Quote", id, "Quote ready", Ann);

        Assert.Equal("{{user}} /  / ", preview.Message);
    }

    // ---- who may send, and how much ----

    [Fact]
    public async Task Sending_needs_access_to_the_document_and_the_right_to_change_it()
    {
        await UserAsync(Ann);
        await UserAsync(Viewer);
        var id = await QuoteAsync(Ann);
        var request = new EmailRequest(["x@example.com"], "Hello", "Text");

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Email.SendFromDocumentAsync("Quote", id, request, Viewer)); // can read, cannot change
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Email.SendFromDocumentAsync("Quote", id, request, new UserContext("eve@example.com", ["Outsider"])));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Email.SendFromDocumentAsync("Quote", "NOPE", request, Ann));
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task Problems_are_reported_by_field()
    {
        await UserAsync(Ann);
        var id = await QuoteAsync(Ann);
        async Task<string> FieldOf(EmailRequest request) =>
            (await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Email.SendFromDocumentAsync("Quote", id, request, Ann))).Errors.First().Field;

        Assert.Equal("to", await FieldOf(new([], "S", "B")));
        Assert.Equal("to", await FieldOf(new(["not an address"], "S", "B")));
        Assert.Equal("to", await FieldOf(new(Enumerable.Range(0, 11).Select(i => $"u{i}@example.com").ToList(), "S", "B")));
        Assert.Equal("subject", await FieldOf(new(["x@example.com"], "", "B")));
        Assert.Equal("subject", await FieldOf(new(["x@example.com"], new string('s', 201), "B")));
        Assert.Equal("message", await FieldOf(new(["x@example.com"], "S", "")));
        Assert.Equal("message", await FieldOf(new(["x@example.com"], "S", new string('b', 20001))));
    }

    [Fact]
    public async Task Nobody_can_send_more_than_fifty_emails_a_day()
    {
        await UserAsync(Ann);
        await UserAsync(Bob);
        var id = await QuoteAsync(Ann);
        for (var i = 0; i < EmailService.DailyLimit / 10; i++)
            await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(Enumerable.Range(0, 10).Select(n => $"u{i}-{n}@example.com").ToList(), "S", "B"), Ann);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "S", "B"), Ann));
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "S", "B"), Bob); // someone else's allowance is their own

        _engine.Clock.Advance(TimeSpan.FromDays(1));
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["x@example.com"], "S", "B"), Ann);
    }

    // ---- notification rules by email ----

    [Fact]
    public async Task A_notification_rule_can_also_email_the_people_it_told()
    {
        await UserAsync(Ann);
        await UserAsync(Bob);
        await _engine.NotificationRuleManager.SaveAsync("Quote", new NotificationRuleSet
        {
            DocType = "Quote",
            Rules = [new NotificationRule { Name = "Tell Bob", Event = NotificationRule.New, Users = [Bob.User], Message = "New quote {{customer}}", Email = true }],
        }, null, TestUsers.SystemManager);

        var id = await QuoteAsync(Ann);

        var mail = Assert.Single(await QueueAsync());
        Assert.Equal(("bob@example.com", "New quote Acme"), ((string)mail["recipient"]!, (string)mail["subject"]!));
        Assert.Contains($"Quote {id}", (string)mail["body"]!);
    }
}
