using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>qahwa.email in server scripts (ADR-164): only to people who have an account, as a write, within limits.</summary>
public class ScriptEmailTests : IAsyncLifetime
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public ScriptEmailTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Parcel", "autoname": "PA-.####", "fields": [ { "name": "weight", "type": "Float" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    public async Task InitializeAsync()
    {
        await _engine.Documents.InsertAsync("User", Values(("email", "bob@example.com"), ("full_name", "Bob")), TestUsers.Admin);
        await _engine.Documents.InsertAsync("User", Values(("email", "gone@example.com"), ("full_name", "Gone"), ("enabled", false)), TestUsers.Admin);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task ScriptAsync(string method, string source)
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Server Script", Values(("title", method), ("active", 1), ("event", "API"), ("method_name", method), ("script", source)), TestUsers.SystemManager);
    }

    private async Task<List<Dictionary<string, object?>>> QueueAsync() =>
        (await _engine.Documents.ListAsync("Email Queue", new ListRequest { Fields = ["recipient", "subject", "body", "reference_doctype", "reference_name", "owner"], Limit = 100 }, TestUsers.Admin)).Data.ToList();

    [Fact]
    public async Task A_script_can_email_a_person_who_has_an_account_about_a_document()
    {
        var parcel = (string)(await _engine.Documents.InsertAsync("Parcel", Values(("weight", 2)), Ann))["name"]!;
        await ScriptAsync("nudge", "qahwa.email('Bob@Example.com', 'Parcel ready', 'Please collect it.', 'Parcel', doc.parcel); result = 'sent';");

        await _engine.ServerScripts.CallMethodAsync("nudge", new Dictionary<string, object?> { ["parcel"] = parcel }, Ann, default);

        var row = Assert.Single(await QueueAsync());
        Assert.Equal("bob@example.com", row["recipient"]);
        Assert.Contains("Parcel ready", (string)row["subject"]!);
        Assert.Equal("Parcel", row["reference_doctype"]);
        Assert.Equal(parcel, row["reference_name"]);
        Assert.Equal("ann@example.com", row["owner"]);
    }

    [Theory]
    [InlineData("stranger@example.com")]
    [InlineData("gone@example.com")]
    [InlineData("not an address")]
    public async Task A_script_cannot_email_strangers_disabled_accounts_or_nonsense(string to)
    {
        await ScriptAsync("mail", "qahwa.email(doc.to, 'Hi', 'Body');");
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.ServerScripts.CallMethodAsync("mail", new Dictionary<string, object?> { ["to"] = to }, Ann, default));
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task A_script_cannot_name_a_document_the_acting_person_may_not_read()
    {
        await ScriptAsync("peek", "qahwa.email('bob@example.com', 'Hi', 'Body', 'Parcel', 'PA-9999');");
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.ServerScripts.CallMethodAsync("peek", new Dictionary<string, object?>(), Ann, default));
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task A_validate_script_may_not_send_email()
    {
        _engine.Clock.Advance(TimeSpan.FromSeconds(1));
        await _engine.Documents.InsertAsync("Server Script", Values(("title", "v"), ("active", 1), ("reference_doctype", "Parcel"), ("event", "Validate"),
            ("script", "qahwa.email('bob@example.com', 'Hi', 'Body');")), TestUsers.SystemManager);
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Parcel", Values(("weight", 1)), Ann));
        Assert.Contains("may not send email", ex.Errors[0].Message);
        Assert.Empty(await QueueAsync());
    }

    [Fact]
    public async Task A_script_may_send_twenty_emails_a_run_and_no_more()
    {
        await ScriptAsync("bulk", "for (var i = 0; i < 25; i++) qahwa.email('bob@example.com', 'Hi ' + i, 'Body');");
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.ServerScripts.CallMethodAsync("bulk", new Dictionary<string, object?>(), Ann, default));
        Assert.Contains("at most 20 emails", ex.Errors[0].Message);
        Assert.Equal(20, (await QueueAsync()).Count);
    }
}
