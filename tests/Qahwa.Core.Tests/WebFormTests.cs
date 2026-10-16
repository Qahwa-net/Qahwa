using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class WebFormTests
{
    private readonly TestEngine _engine = new();

    public WebFormTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Enquiry", "fields": [
                { "name": "full_name", "type": "Data", "required": true },
                { "name": "email", "type": "Email" },
                { "name": "message", "type": "Text" },
                { "name": "kind", "type": "Select", "options": "Sales\nSupport" },
                { "name": "staff_notes", "type": "Text", "permLevel": 1 },
                { "name": "secret", "type": "Password" },
                { "name": "owner_ref", "type": "Link", "options": "Customer" },
                { "name": "internal", "type": "Data", "hidden": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private async Task FormAsync(string route = "contact", string fields = "full_name\nemail\nmessage\nkind", bool published = true, string doctype = "Enquiry", int perHour = 10)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Web Form", Values(("route", route), ("title", "Contact us"), ("published", published ? 1 : 0), ("doc_type", doctype), ("fields", fields), ("max_per_hour", perHour)), TestUsers.SystemManager);
    }

    private Task<string> SendAsync(IReadOnlyDictionary<string, object?> values, string route = "contact", string source = "1.2.3.4") =>
        _engine.WebForms.SubmitAsync(route, values, source);

    private async Task<IReadOnlyList<Dictionary<string, object?>>> EnquiriesAsync() =>
        (await _engine.Documents.ListAsync("Enquiry", new ListRequest { Fields = ["name", "owner", "full_name", "message"], Limit = 100 }, TestUsers.Admin)).Data;

    [Fact]
    public async Task Anyone_can_open_and_send_a_published_form()
    {
        await FormAsync();

        var form = await _engine.WebForms.GetAsync("contact");
        Assert.Equal(["full_name", "email", "message", "kind"], form.Fields.Select(f => f.Name));
        Assert.True(form.Fields[0].Required);
        Assert.Equal(["Sales", "Support"], form.Fields[3].Options);

        var message = await SendAsync(Values(("full_name", "Sam"), ("message", "Hello"), ("kind", "Sales")));

        Assert.Contains("Thank you", message);
        var saved = Assert.Single(await EnquiriesAsync());
        Assert.Equal("Sam", saved["full_name"]);
        Assert.Equal(WebFormService.WebUser, saved["owner"]);
        Assert.DoesNotContain((string)saved["name"]!, message); // nothing about the stored document is given back
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("")]
    [InlineData("Contact")]
    [InlineData("../etc/passwd")]
    [InlineData("a b")]
    public async Task Missing_and_malformed_addresses_are_not_available(string route)
    {
        await FormAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebForms.GetAsync(route));
        await Assert.ThrowsAsync<NotFoundException>(() => SendAsync(Values(("full_name", "x")), route));
    }

    [Fact]
    public async Task A_form_that_is_not_published_is_not_there()
    {
        await FormAsync(published: false);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebForms.GetAsync("contact"));
        await Assert.ThrowsAsync<NotFoundException>(() => SendAsync(Values(("full_name", "x"))));
    }

    [Theory]
    [InlineData("full_name\nstaff_notes")] // a higher permission level
    [InlineData("full_name\nsecret")] // a password
    [InlineData("full_name\nowner_ref")] // a link (would let visitors probe other documents)
    [InlineData("full_name\ninternal")] // hidden
    [InlineData("full_name\nowner")] // not a field of the DocType
    [InlineData("full_name\nnot_there")]
    [InlineData("email")] // leaves out a required field
    [InlineData("")]
    public async Task A_form_that_asks_for_something_unsafe_or_cannot_work_fails_closed(string fields)
    {
        await FormAsync(fields: fields);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.WebForms.GetAsync("contact"));
        await Assert.ThrowsAsync<NotFoundException>(() => SendAsync(Values(("full_name", "x"))));
        Assert.Empty(await EnquiriesAsync());
    }

    [Fact]
    public async Task A_form_cannot_create_framework_documents()
    {
        await FormAsync(fields: "role_name", doctype: "Role");

        await Assert.ThrowsAsync<NotFoundException>(() => SendAsync(Values(("role_name", "Hacker"))));
    }

    [Fact]
    public async Task Fields_the_form_does_not_ask_for_are_refused_and_nothing_is_saved()
    {
        await FormAsync();

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("full_name", "Sam"), ("owner", "Administrator"), ("docstatus", 1))));
        Assert.Contains(error.Errors, e => e.Field == "owner");
        await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("full_name", "Sam"), ("staff_notes", "x"))));
        Assert.Empty(await EnquiriesAsync());
    }

    [Fact]
    public async Task Answers_are_checked_like_any_other_document_and_only_their_errors_are_shown()
    {
        await FormAsync();

        var missing = await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("message", "no name"))));
        Assert.Contains(missing.Errors, e => e.Field == "full_name");
        var badChoice = await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("full_name", "Sam"), ("kind", "Other"))));
        Assert.Contains(badChoice.Errors, e => e.Field == "kind");
    }

    [Fact]
    public async Task Huge_and_odd_values_are_refused()
    {
        await FormAsync();

        await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("full_name", new string('x', WebFormService.MaxValueLength + 1)))));
        await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("full_name", new[] { "a", "b" }))));
        await Assert.ThrowsAsync<DocumentValidationException>(() => SendAsync(Values(("full_name", new Dictionary<string, object?> { ["a"] = 1 }))));
        Assert.Empty(await EnquiriesAsync());
    }

    [Fact]
    public async Task Markup_in_answers_is_stored_as_text_and_never_changed()
    {
        await FormAsync();

        await SendAsync(Values(("full_name", "<script>alert(1)</script>"), ("message", "'; drop table \"tabEnquiry\"; --")));

        var saved = Assert.Single(await EnquiriesAsync());
        Assert.Equal("<script>alert(1)</script>", saved["full_name"]);
        Assert.Equal("'; drop table \"tabEnquiry\"; --", saved["message"]);
    }

    [Fact]
    public async Task The_hidden_trap_field_fools_bots_without_saving_anything()
    {
        await FormAsync();

        var message = await SendAsync(Values(("full_name", "Bot"), (WebFormService.Honeypot, "http://spam.example")));

        Assert.Contains("Thank you", message); // looks like success
        Assert.Empty(await EnquiriesAsync());
    }

    [Fact]
    public async Task One_source_cannot_flood_a_form_but_others_can_still_send()
    {
        await FormAsync(perHour: 3);

        for (var i = 0; i < 3; i++)
            await SendAsync(Values(("full_name", $"A{i}")), source: "9.9.9.9");
        await Assert.ThrowsAsync<RateLimitedException>(() => SendAsync(Values(("full_name", "A3")), source: "9.9.9.9"));
        await SendAsync(Values(("full_name", "B")), source: "8.8.8.8");
        Assert.Equal(4, (await EnquiriesAsync()).Count);

        _engine.Clock.Advance(TimeSpan.FromHours(2)); // the window moves on
        await SendAsync(Values(("full_name", "A4")), source: "9.9.9.9");
    }

    [Fact]
    public async Task Rejected_attempts_that_hit_the_limit_do_not_reach_the_database()
    {
        await FormAsync(perHour: 1);
        await SendAsync(Values(("full_name", "First")));

        await Assert.ThrowsAsync<RateLimitedException>(() => SendAsync(Values(("full_name", "Second"))));

        Assert.Single(await EnquiriesAsync());
    }

    [Fact]
    public void Addresses_are_plain()
    {
        Assert.True(WebFormService.IsRoute("contact-us-2"));
        foreach (var bad in new[] { "-a", "A", "a_b", "a/b", "a.b", new string('a', 70), "" })
            Assert.False(WebFormService.IsRoute(bad));
    }
}
