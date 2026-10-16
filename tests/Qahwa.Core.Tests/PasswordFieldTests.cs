using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class PasswordFieldTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Vic = new("vic@example.com", ["Viewer"]);

    public PasswordFieldTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Mail Account", "autoname": "field:label",
              "fields": [ { "name": "label", "type": "Data", "required": true }, { "name": "secret", "type": "Password" }, { "name": "host", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Viewer", "read": true } ] }
            """));
    }

    private async Task<string?> StoredAsync(string name) =>
        (await _engine.Repository.GetAsync(_engine.Registry.Get("Mail Account"), name)) is { } row ? row["secret"] as string : null;

    [Fact]
    public async Task The_secret_is_stored_encrypted_and_shown_only_as_a_mask()
    {
        var doc = await _engine.Documents.InsertAsync("Mail Account", Values(("label", "main"), ("secret", "hunter2")), Ann);

        Assert.Equal(DocumentService.PasswordMask, doc["secret"]);
        var stored = await StoredAsync("main");
        Assert.NotNull(stored);
        Assert.DoesNotContain("hunter2", stored);
        Assert.Equal(DocumentService.PasswordMask, (await _engine.Documents.GetAsync("Mail Account", "main", Vic))["secret"]);
    }

    [Fact]
    public async Task Sending_the_mask_back_keeps_the_secret_and_a_new_value_replaces_it()
    {
        await _engine.Documents.InsertAsync("Mail Account", Values(("label", "main"), ("secret", "one")), Ann);

        await _engine.Documents.UpdateAsync("Mail Account", "main", Values(("secret", DocumentService.PasswordMask), ("host", "mx")), Ann);
        Assert.Equal("one", await _engine.Documents.GetPasswordAsync("Mail Account", "main", "secret", Ann));

        await _engine.Documents.UpdateAsync("Mail Account", "main", Values(("secret", "two")), Ann);
        Assert.Equal("two", await _engine.Documents.GetPasswordAsync("Mail Account", "main", "secret", Ann));

        var cleared = await _engine.Documents.UpdateAsync("Mail Account", "main", Values(("secret", "")), Ann);
        Assert.Null(cleared["secret"]);
        Assert.Null(await _engine.Documents.GetPasswordAsync("Mail Account", "main", "secret", Ann));
    }

    [Fact]
    public async Task The_secret_cannot_be_read_back_by_listing_filtering_sorting_or_without_the_right_to_change_the_document()
    {
        await _engine.Documents.InsertAsync("Mail Account", Values(("label", "main"), ("secret", "hunter2")), Ann);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ListAsync("Mail Account", new ListRequest { Fields = ["name", "secret"] }, Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ListAsync("Mail Account", new ListRequest { Filters = [new RawFilter("secret", "like", "%")] }, Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.ListAsync("Mail Account", new ListRequest { OrderBy = "secret" }, Ann));
        var all = (await _engine.Documents.ListAsync("Mail Account", new ListRequest(), Ann)).Data.Single();
        Assert.False(all.ContainsKey("secret"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetPasswordAsync("Mail Account", "main", "secret", Vic));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.GetPasswordAsync("Mail Account", "main", "host", Ann));
    }

    [Fact]
    public async Task A_secret_that_is_too_long_or_not_text_is_refused()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Mail Account", Values(("label", "x"), ("secret", new string('a', 1001))), Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Mail Account", Values(("label", "y"), ("secret", 12345)), Ann));
    }

    [Fact]
    public void A_child_table_cannot_have_a_password_field()
    {
        var ex = Assert.ThrowsAny<Exception>(() => DocTypeLoader.Parse("""
            { "name": "Bad Line", "isChildTable": true, "fields": [ { "name": "pin", "type": "Password" } ] }
            """));
        Assert.Contains("Password", ex.Message);
    }
}
