using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class ContactAddressTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Boss = new("boss@example.com", ["System Manager"]);

    public ContactAddressTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Account", "autoname": "field:customer_name", "fields": [ { "name": "customer_name", "type": "Data", "required": true } ],
              "permissions": [ { "role": "System Manager", "read": true, "write": true, "create": true } ] }
            """));
    }

    [Fact]
    public async Task A_contact_has_a_full_name_and_can_be_linked_to_any_document()
    {
        await _engine.Documents.InsertAsync("Account", Values(("customer_name", "Acme")), Boss);

        var contact = await _engine.Documents.InsertAsync("Contact", Values(("first_name", "Ann"), ("last_name", "Lee"), ("email_id", "ann@acme.example"), ("phone", "+966 50 123 4567"),
            ("links", new object[] { Values(("link_doctype", "Account"), ("link_name", "Acme")) })), Boss);

        Assert.StartsWith("CON-", (string)contact["name"]!);
        Assert.Equal("Ann Lee", contact["full_name"]);
        var rows = ((IEnumerable<Dictionary<string, object?>>)contact["links"]!).ToList();
        Assert.Equal(("Account", "Acme"), ((string)rows[0]["link_doctype"]!, (string)rows[0]["link_name"]!));
        var alone = await _engine.Documents.InsertAsync("Contact", Values(("first_name", "Bob")), Boss);
        Assert.Equal("Bob", alone["full_name"]);
    }

    [Fact]
    public async Task A_contact_needs_a_first_name_and_a_valid_email_and_a_link_must_point_at_something_that_exists()
    {
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Contact", Values(("last_name", "Lee")), Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Contact", Values(("first_name", "Ann"), ("email_id", "not-an-email")), Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Contact", Values(("first_name", "Ann"),
            ("links", new object[] { Values(("link_doctype", "Account"), ("link_name", "Nobody")) })), Boss));
    }

    [Fact]
    public async Task An_address_has_a_type_and_needs_a_line_and_a_city_and_only_managers_may_use_it_by_default()
    {
        var address = await _engine.Documents.InsertAsync("Address", Values(("address_title", "Head office"), ("address_line1", "1 King Road"), ("city", "Riyadh")), Boss);

        Assert.Equal("Billing", address["address_type"]);
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Address", Values(("city", "Riyadh")), Boss));
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Address", Values(("address_line1", "x"), ("city", "y"), ("address_type", "Moon")), Boss));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Documents.GetAsync("Address", (string)address["name"]!, new UserContext("c@example.com", ["Clerk"])));
    }
}
