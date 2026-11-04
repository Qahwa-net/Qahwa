using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

/// <summary>Email threads on documents: what is sent from a document and the replies that come back to it (ADR-153).</summary>
public class ConversationTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Outsider = new("eve@example.com", ["Other"]);

    public ConversationTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Quote", "fields": [ { "name": "customer", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "email": true } ] }
            """));
    }

    private async Task<string> QuoteAsync() => (string)(await _engine.Documents.InsertAsync("Quote", Values(("customer", "Acme")), Ann))["name"]!;

    private Task SetUpMailboxAsync() => _engine.Documents.UpdateAsync("Email Account", "Email Account", Values(
        ("enabled", false), ("imap_enabled", true), ("imap_server", "imap.example.com"), ("imap_port", 993), ("imap_username", "desk@example.com"),
        ("imap_password", "imap-secret"), ("imap_folder", "INBOX")), TestUsers.Admin);

    private static ReceivedMail Reply(long uid, string subject, string from = "buyer@example.com", string text = "Yes, please go ahead.") =>
        new(uid, $"<r{uid}@example.com>", null, from, "Buyer", subject, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), text);

    private async Task<string> SentSubjectAsync() =>
        (string)(await _engine.Documents.ListAsync("Email Queue", new ListRequest { Fields = ["subject"], Limit = 1 }, TestUsers.Admin)).Data.Single()["subject"]!;

    [Fact]
    public async Task A_reply_that_keeps_the_reference_lands_on_the_document_and_shows_after_what_was_sent()
    {
        var id = await QuoteAsync();
        await SetUpMailboxAsync();
        await _engine.IncomingMail.PollAsync(); // the first poll only notes where the folder ends
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["buyer@example.com"], "Your quote", "Here it is."), Ann);
        var subject = await SentSubjectAsync();
        Assert.Matches(@"^Your quote \[QW-[0-9a-f]{10}\]$", subject);

        _engine.Mailbox.Add(Reply(2, "Re: " + subject));
        _engine.Mailbox.Add(Reply(3, "Something else entirely"));
        Assert.Equal(2, await _engine.IncomingMail.PollAsync());

        var thread = await _engine.Conversation.ForDocumentAsync("Quote", id, Ann);
        Assert.Equal(["sent", "received"], thread.Select(t => t.Kind));
        Assert.Equal("Your quote", thread[0].Subject);                // the reference is not shown to people
        Assert.Equal("Re: Your quote", thread[1].Subject);
        Assert.Equal("Yes, please go ahead.", thread[1].Text);
        Assert.Equal("buyer@example.com", thread[1].Who);
        Assert.Equal("Waiting", thread[0].Status);                    // not delivered yet: the queue has not run
    }

    [Fact]
    public async Task One_send_to_several_people_is_one_message_and_mail_without_a_known_reference_is_not_attached()
    {
        var id = await QuoteAsync();
        await SetUpMailboxAsync();
        await _engine.IncomingMail.PollAsync();
        await _engine.Email.SendFromDocumentAsync("Quote", id, new EmailRequest(["a@example.com", "b@example.com"], "Hello", "Text"), Ann);
        _engine.Mailbox.Add(Reply(2, "Re: Hello [QW-0000000000]"));    // a reference nobody was given
        await _engine.IncomingMail.PollAsync();

        var thread = await _engine.Conversation.ForDocumentAsync("Quote", id, Ann);

        var only = Assert.Single(thread);
        Assert.Equal("a@example.com, b@example.com", only.Who);
    }

    [Fact]
    public async Task Only_people_who_can_read_the_document_see_its_conversation()
    {
        var id = await QuoteAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Conversation.ForDocumentAsync("Quote", id, Outsider));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Conversation.ForDocumentAsync("Quote", id, UserContext.Anonymous));
        Assert.Empty(await _engine.Conversation.ForDocumentAsync("Quote", id, Ann));
    }
}
