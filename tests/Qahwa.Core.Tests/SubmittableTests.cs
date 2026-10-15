using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class SubmittableTests
{
    private readonly TestEngine _engine = new();

    private static readonly UserContext Clerk = new("clerk@example.com", ["Clerk"]);
    private static readonly UserContext Approver = new("boss@example.com", ["Approver"]);

    public SubmittableTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Invoice Line", "isChildTable": true, "fields": [ { "name": "item", "type": "Data" }, { "name": "qty", "type": "Int" } ] }
            """));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            {
              "name": "Invoice", "isSubmittable": true,
              "fields": [
                { "name": "customer", "type": "Data", "required": true },
                { "name": "total", "type": "Currency" },
                { "name": "remarks", "type": "Data", "allowOnSubmit": true },
                { "name": "lines", "type": "Table", "options": "Invoice Line" }
              ],
              "permissions": [
                { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true },
                { "role": "Approver", "read": true, "write": true, "submit": true, "cancel": true }
              ]
            }
            """));
    }

    private async Task<string> DraftAsync(string customer = "Acme") =>
        (string)(await _engine.Documents.InsertAsync("Invoice", Values(("customer", customer), ("total", 10)), Clerk))["name"]!;

    [Fact]
    public async Task Only_people_with_the_submit_right_can_submit_and_only_drafts()
    {
        var id = await DraftAsync();

        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.SubmitAsync("Invoice", id, Clerk));
        var submitted = await _engine.Documents.SubmitAsync("Invoice", id, Approver);

        Assert.Equal(1, submitted["docstatus"]);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Invoice", id, Approver));
    }

    [Fact]
    public async Task Submitting_checks_the_document_like_a_save()
    {
        var id = await DraftAsync();
        // A required field emptied through the repository, as if the rules changed since the draft was saved.
        await _engine.Repository.UpdateAsync(_engine.Registry.Get("Invoice"), id, new Dictionary<string, object?> { ["customer"] = null }, null);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Invoice", id, Approver));

        Assert.Contains(ex.Errors, e => e.Field == "customer");
    }

    [Fact]
    public async Task A_submitted_document_only_accepts_changes_to_allow_on_submit_fields()
    {
        var id = await DraftAsync();
        await _engine.Documents.SubmitAsync("Invoice", id, Approver);

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Invoice", id, Values(("total", 99)), Approver));
        Assert.Contains(ex.Errors, e => e.Field == "total");
        await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Invoice", id, Values(("lines", new[] { Values(("item", "x"), ("qty", 1)) })), Approver));

        var ok = await _engine.Documents.UpdateAsync("Invoice", id, Values(("remarks", "paid")), Approver);
        Assert.Equal("paid", ok["remarks"]);
        Assert.Equal(1, ok["docstatus"]);
    }

    [Fact]
    public async Task Cancelling_locks_the_document_for_good_and_needs_the_cancel_right()
    {
        var id = await DraftAsync();
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.CancelAsync("Invoice", id, Approver)); // still a draft
        await _engine.Documents.SubmitAsync("Invoice", id, Approver);
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.CancelAsync("Invoice", id, Clerk));

        var cancelled = await _engine.Documents.CancelAsync("Invoice", id, Approver);

        Assert.Equal(2, cancelled["docstatus"]);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Invoice", id, Values(("remarks", "x")), Approver));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Invoice", id, Approver));
        await _engine.Documents.DeleteAsync("Invoice", id, Clerk); // cancelled documents can be removed
    }

    [Fact]
    public async Task Submitted_documents_cannot_be_deleted()
    {
        var id = await DraftAsync();
        await _engine.Documents.SubmitAsync("Invoice", id, Approver);

        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.DeleteAsync("Invoice", id, TestUsers.Admin));
    }

    [Fact]
    public async Task A_stale_modified_value_stops_the_transition()
    {
        var id = await DraftAsync();
        var seen = (DateTime)(await _engine.Documents.GetAsync("Invoice", id, Clerk))["modified"]!;
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Invoice", id, Values(("total", 20)), Clerk);

        await Assert.ThrowsAsync<ConflictException>(() => _engine.Documents.SubmitAsync("Invoice", id, Approver, seen));
    }

    [Fact]
    public async Task Ordinary_doctypes_cannot_be_submitted_and_effective_permissions_show_the_rights()
    {
        var customer = (string)(await _engine.Documents.InsertAsync("Customer", Values(("customer_name", "X")), TestUsers.Admin))["name"]!;
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.SubmitAsync("Customer", customer, TestUsers.Admin));

        var view = _engine.Documents.GetMetaView("Invoice", Approver);
        Assert.True(view.Permissions.Submit);
        Assert.True(view.Permissions.Cancel);
        Assert.False(_engine.Documents.GetMetaView("Invoice", Clerk).Permissions.Submit);
    }

    [Fact]
    public void Definitions_are_checked()
    {
        var childSubmittable = Assert.Throws<DocTypeValidationException>(() =>
            DocTypeLoader.Parse("""{ "name": "X", "isChildTable": true, "isSubmittable": true, "fields": [ { "name": "a", "type": "Data" } ] }"""));
        Assert.Contains("child table", childSubmittable.Message);

        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""
            { "name": "Y", "fields": [ { "name": "a", "type": "Data", "allowOnSubmit": true } ] }
            """));
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""
            { "name": "Z", "fields": [ { "name": "a", "type": "Data" } ], "permissions": [ { "role": "R", "read": true, "submit": true } ] }
            """));
    }
}
