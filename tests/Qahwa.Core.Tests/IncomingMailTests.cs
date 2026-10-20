using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class IncomingMailTests
{
    private readonly TestEngine _engine = new();

    private Task SetUpAsync(bool enabled = true, string? password = "imap-secret") =>
        _engine.Documents.UpdateAsync("Email Account", "Email Account", Values(
            ("enabled", false), ("imap_enabled", enabled), ("imap_server", "imap.example.com"), ("imap_port", 993), ("imap_username", "desk@example.com"),
            ("imap_password", password), ("imap_folder", "INBOX")), TestUsers.Admin);

    private static ReceivedMail Mail(long uid, string from = "ann@example.com", string subject = "Hello", string text = "Body", string? id = null) =>
        new(uid, id ?? $"<m{uid}@example.com>", null, from, "Ann", subject, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), text);

    private async Task<List<Dictionary<string, object?>>> KeptAsync() =>
        (await _engine.Documents.ListAsync("Communication", new ListRequest { Fields = ["name", "sender", "subject", "content", "message_id"], OrderBy = "creation", Descending = false, Limit = 200 }, TestUsers.Admin)).Data.ToList();

    /// <summary>The first poll only notes where the folder ends, so mail that was already there is not imported.</summary>
    private async Task StartAsync()
    {
        await SetUpAsync();
        Assert.Equal(0, await _engine.IncomingMail.PollAsync());
    }

    [Fact]
    public async Task Nothing_happens_while_incoming_mail_is_off_or_incomplete()
    {
        _engine.Mailbox.Add(Mail(1));
        Assert.Equal(0, await _engine.IncomingMail.PollAsync());
        await SetUpAsync(enabled: false);
        Assert.Equal(0, await _engine.IncomingMail.PollAsync());
        Assert.Null(_engine.Mailbox.LastSettings);
        Assert.Empty(await KeptAsync());
    }

    [Fact]
    public async Task Enabling_needs_a_password_and_a_sensible_port()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => SetUpAsync(password: null));
        await SetUpAsync();
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Email Account", "Email Account", Values(("imap_port", 70000)), TestUsers.Admin));
    }

    [Fact]
    public async Task Existing_mail_is_not_imported_but_new_mail_is_kept()
    {
        _engine.Mailbox.Add(Mail(1, subject: "Old"));
        await StartAsync();
        Assert.Empty(await KeptAsync());

        _engine.Mailbox.Add(Mail(2, subject: "New one", text: "Hello team"));
        Assert.Equal(1, await _engine.IncomingMail.PollAsync());
        var kept = Assert.Single(await KeptAsync());
        Assert.Equal("ann@example.com", kept["sender"]);
        Assert.Equal("New one", kept["subject"]);
        Assert.Equal("Hello team", kept["content"]);
        Assert.Equal("imap-secret", _engine.Mailbox.LastSettings!.Password);

        Assert.Equal(0, await _engine.IncomingMail.PollAsync());
        Assert.Equal(2, _engine.Mailbox.LastAfterUid);
        Assert.Single(await KeptAsync());
    }

    [Fact]
    public async Task The_same_message_is_kept_once()
    {
        await StartAsync();
        _engine.Mailbox.Add(Mail(5, id: "<same@example.com>"));
        _engine.Mailbox.Add(Mail(6, id: "<same@example.com>"));
        Assert.Equal(1, await _engine.IncomingMail.PollAsync());
        Assert.Single(await KeptAsync());
    }

    [Fact]
    public async Task A_renumbered_folder_starts_again_without_importing_everything()
    {
        _engine.Mailbox.Add(Mail(1));
        await StartAsync();
        _engine.Mailbox.UidValidity = 2;
        _engine.Mailbox.Add(Mail(2));
        Assert.Equal(0, await _engine.IncomingMail.PollAsync());
        Assert.Empty(await KeptAsync());
        _engine.Mailbox.Add(Mail(3));
        Assert.Equal(1, await _engine.IncomingMail.PollAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("not an address")]
    [InlineData("a@b.com\r\nBcc: evil@example.com")]
    [InlineData("Ann <ann@example.com>")]
    public async Task Messages_without_a_proper_sender_are_dropped(string sender)
    {
        await StartAsync();
        _engine.Mailbox.Add(Mail(2, from: sender));
        Assert.Equal(0, await _engine.IncomingMail.PollAsync());
        Assert.Empty(await KeptAsync());
    }

    [Fact]
    public async Task Hostile_content_is_kept_as_harmless_text_of_limited_size()
    {
        await StartAsync();
        var subject = "Hi\u0000\u0007 <script>alert(1)</script>" + new string('x', 1000);
        var text = "Line 1\r\nLine 2\u0000\u001b[31m" + new string('y', 200_000);
        _engine.Mailbox.Add(Mail(2, subject: subject, text: text));
        Assert.Equal(1, await _engine.IncomingMail.PollAsync());
        var kept = Assert.Single(await KeptAsync());
        var savedSubject = (string)kept["subject"]!;
        var savedText = (string)kept["content"]!;
        Assert.True(savedSubject.Length <= IncomingMailService.MaxSubject + 1);
        Assert.DoesNotContain('\u0000', savedSubject);
        Assert.DoesNotContain('\u0007', savedSubject);
        Assert.DoesNotContain('\u001b', savedText);
        Assert.DoesNotContain('\r', savedText);
        Assert.Contains("Line 1\nLine 2", savedText);
        Assert.True(savedText.Length <= IncomingMailService.MaxText + 1);
    }

    [Fact]
    public async Task Only_fifty_messages_are_taken_each_time_and_the_rest_follow()
    {
        await StartAsync();
        for (var i = 2; i < 2 + 60; i++) _engine.Mailbox.Add(Mail(i));
        Assert.Equal(IncomingMailService.MaxPerPoll, await _engine.IncomingMail.PollAsync());
        Assert.Equal(10, await _engine.IncomingMail.PollAsync());
        Assert.Equal(60, (await KeptAsync()).Count);
    }

    [Fact]
    public async Task A_broken_server_is_an_error_not_lost_mail()
    {
        await StartAsync();
        _engine.Mailbox.Add(Mail(2));
        _engine.Mailbox.Broken = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _engine.IncomingMail.PollAsync());
        _engine.Mailbox.Broken = false;
        Assert.Equal(1, await _engine.IncomingMail.PollAsync());
    }

    [Fact]
    public async Task Communications_are_for_system_managers_only_and_the_password_is_not_shown()
    {
        await StartAsync();
        _engine.Mailbox.Add(Mail(2));
        await _engine.IncomingMail.PollAsync();
        var clerk = new UserContext("clerk@example.com", ["Clerk"]);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.ListAsync("Communication", new ListRequest { Limit = 10 }, clerk));
        var account = await _engine.Documents.GetAsync("Email Account", "Email Account", TestUsers.Admin);
        Assert.Equal(DocumentService.PasswordMask, account["imap_password"]);
    }

    [Fact]
    public async Task The_connection_test_reports_the_folder_size_or_asks_for_settings()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.IncomingMail.TestAsync());
        await SetUpAsync();
        _engine.Mailbox.Add(Mail(1));
        _engine.Mailbox.Add(Mail(2));
        Assert.Equal(2, await _engine.IncomingMail.TestAsync());
    }
}
