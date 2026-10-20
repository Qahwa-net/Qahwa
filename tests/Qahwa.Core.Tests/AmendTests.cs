using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AmendTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private static readonly UserContext Approver = new("boss@example.com", ["Approver"]);
    private static readonly UserContext Nosy = new("nosy@example.com", ["Nosy"]);

    public AmendTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Bill Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Bill", "isSubmittable": true,
              "fields": [ { "name": "customer", "type": "Data", "required": true }, { "name": "ref_note", "type": "Data", "noCopy": true }, { "name": "lines", "type": "Table", "options": "Bill Line" } ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true },
                { "role": "Approver", "read": true, "write": true, "submit": true, "cancel": true },
                { "role": "Nosy", "read": true, "create": true, "amend": false } ] }
            """));
    }

    private async Task<string> CancelledAsync()
    {
        var draft = await _engine.Documents.InsertAsync("Bill", Values(("customer", "Acme"), ("lines", new[] { Values(("item", "pen"), ("qty", 2)) })), Clerk);
        var name = (string)draft["name"]!;
        await _engine.Documents.SubmitAsync("Bill", name, Approver);
        await _engine.Documents.CancelAsync("Bill", name, Approver);
        return name;
    }

    [Fact]
    public async Task A_cancelled_document_is_amended_into_a_linked_draft_with_its_rows()
    {
        var old = await CancelledAsync();
        var amended = await _engine.Documents.AmendAsync("Bill", old, Clerk);
        Assert.Equal(old, amended["amended_from"]);
        Assert.Equal(0, Convert.ToInt32(amended["docstatus"]));
        var read = await _engine.Documents.GetAsync("Bill", (string)amended["name"]!, Clerk);
        Assert.Equal("Acme", read["customer"]);
        Assert.Equal(old, read["amended_from"]);
        Assert.Single((IEnumerable<object?>)read["lines"]!);
    }

    [Fact]
    public async Task A_no_copy_field_starts_empty_in_the_amended_document()
    {
        var draft = await _engine.Documents.InsertAsync("Bill", Values(("customer", "Acme"), ("ref_note", "secret ref")), Clerk);
        var name = (string)draft["name"]!;
        await _engine.Documents.SubmitAsync("Bill", name, Approver);
        await _engine.Documents.CancelAsync("Bill", name, Approver);
        var amended = await _engine.Documents.AmendAsync("Bill", name, Clerk);
        Assert.Equal("Acme", (await _engine.Documents.GetAsync("Bill", (string)amended["name"]!, Clerk))["customer"]);
        Assert.Null((await _engine.Documents.GetAsync("Bill", (string)amended["name"]!, Clerk)).GetValueOrDefault("ref_note"));
    }

    [Fact]
    public async Task Only_a_cancelled_document_is_amended_and_only_once()
    {
        var draft = (string)(await _engine.Documents.InsertAsync("Bill", Values(("customer", "x")), Clerk))["name"]!;
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.AmendAsync("Bill", draft, Clerk));
        var old = await CancelledAsync();
        await _engine.Documents.AmendAsync("Bill", old, Clerk);
        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.AmendAsync("Bill", old, Clerk));
    }

    [Fact]
    public async Task The_amend_right_can_be_withheld_and_reading_is_needed()
    {
        var old = await CancelledAsync();
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.AmendAsync("Bill", old, Nosy));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.AmendAsync("Bill", old, new UserContext("who@example.com", ["Other"])));
    }

    [Fact]
    public async Task A_client_cannot_claim_a_document_was_amended()
    {
        var old = await CancelledAsync();
        var other = (string)(await _engine.Documents.InsertAsync("Bill", Values(("customer", "y"), ("amended_from", old)), Clerk))["name"]!;
        Assert.Null((await _engine.Documents.GetAsync("Bill", other, Clerk)).GetValueOrDefault("amended_from"));
        await _engine.Documents.AmendAsync("Bill", old, Clerk);
    }

    [Fact]
    public void Only_submittable_types_get_the_link()
    {
        Assert.Contains(_engine.Registry.Get("Bill").Fields, f => f.Name == "amended_from");
        Assert.DoesNotContain(DocTypeLoader.Parse("""{ "name": "Plain", "fields": [ { "name": "a", "type": "Data" } ] }""").Fields, f => f.Name == "amended_from");
    }
}
