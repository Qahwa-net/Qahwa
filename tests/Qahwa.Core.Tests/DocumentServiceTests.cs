using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DocumentServiceTests
{
    private readonly TestEngine _engine = new();

    private Task<Dictionary<string, object?>> InsertCustomerAsync(IUserContext user, string name, params (string, object?)[] extra) =>
        _engine.Documents.InsertAsync(
            "Customer",
            Values([("customer_name", name), .. extra]),
            user);

    [Fact]
    public async Task Insert_names_from_series_sets_standard_fields_and_defaults()
    {
        var first = await InsertCustomerAsync(TestUsers.Manager, "Acme");
        var second = await InsertCustomerAsync(TestUsers.Manager, "Globex");

        Assert.Equal("CUST-00001", first["name"]);
        Assert.Equal("CUST-00002", second["name"]);
        Assert.Equal("manager@example.com", first["owner"]);
        Assert.Equal("manager@example.com", first["modified_by"]);
        Assert.Equal("Company", first["customer_type"]);
        Assert.Equal(true, first["active"]);
        Assert.Equal(0, first["docstatus"]);
    }

    [Fact]
    public async Task Insert_collects_all_validation_errors()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.InsertAsync(
                "Customer",
                Values(("customer_type", "Government"), ("bogus", 1), ("territory", "Nowhere")),
                TestUsers.Manager));

        var fields = ex.Errors.Select(e => e.Field).ToHashSet();
        Assert.Contains("customer_name", fields); // required
        Assert.Contains("customer_type", fields); // not a valid option
        Assert.Contains("bogus", fields);         // unknown
        Assert.Contains("territory", fields);     // link target missing
    }

    [Fact]
    public async Task Insert_rejects_values_of_the_wrong_type()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            InsertCustomerAsync(TestUsers.Manager, "Acme", ("credit_limit", "lots"), ("since", "not a date")));

        Assert.Contains(ex.Errors, e => e.Field == "credit_limit");
        Assert.Contains(ex.Errors, e => e.Field == "since");
    }

    [Fact]
    public async Task Insert_accepts_existing_link_target()
    {
        await _engine.Documents.InsertAsync("Territory", Values(("territory_name", "Riyadh")), TestUsers.Manager);

        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme", ("territory", "Riyadh"));

        Assert.Equal("Riyadh", doc["territory"]);
    }

    [Fact]
    public async Task Insert_ignores_client_supplied_standard_fields()
    {
        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme", ("owner", "someone.else"), ("name", "HACK-1"));

        Assert.Equal("manager@example.com", doc["owner"]);
        Assert.Equal("CUST-00001", doc["name"]);
    }

    [Fact]
    public async Task Duplicate_unique_value_is_a_conflict()
    {
        await InsertCustomerAsync(TestUsers.Manager, "Acme");

        await Assert.ThrowsAsync<ConflictException>(() => InsertCustomerAsync(TestUsers.Manager, "Acme"));
    }

    [Fact]
    public async Task Guest_is_unauthenticated_and_other_roles_are_denied()
    {
        await Assert.ThrowsAsync<UnauthenticatedException>(() => InsertCustomerAsync(UserContext.Anonymous, "Acme"));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => InsertCustomerAsync(TestUsers.Nobody, "Acme"));
    }

    [Fact]
    public async Task Unknown_doctype_is_not_found()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            _engine.Documents.GetAsync("Nope", "x", TestUsers.Admin));
    }

    [Fact]
    public async Task If_owner_rule_limits_list_and_get_to_own_documents()
    {
        var mine = await InsertCustomerAsync(TestUsers.SalesA, "A Corp");
        var theirs = await InsertCustomerAsync(TestUsers.SalesB, "B Corp");

        var list = await _engine.Documents.ListAsync("Customer", new ListRequest(), TestUsers.SalesA);

        Assert.Equal(["A Corp"], list.Data.Select(r => (string)r["customer_name"]!));
        Assert.Equal(1, list.Total);
        Assert.NotNull(await _engine.Documents.GetAsync("Customer", (string)mine["name"]!, TestUsers.SalesA));
        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.GetAsync("Customer", (string)theirs["name"]!, TestUsers.SalesA));
    }

    [Fact]
    public async Task Role_without_delete_cannot_delete()
    {
        var doc = await InsertCustomerAsync(TestUsers.SalesA, "A Corp");

        await Assert.ThrowsAsync<PermissionDeniedException>(() =>
            _engine.Documents.DeleteAsync("Customer", (string)doc["name"]!, TestUsers.SalesA));
    }

    [Fact]
    public async Task Update_changes_only_supplied_fields_and_bumps_modified()
    {
        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme", ("credit_limit", 100));
        _engine.Clock.Advance(TimeSpan.FromMinutes(5));

        var updated = await _engine.Documents.UpdateAsync(
            "Customer", (string)doc["name"]!, Values(("customer_type", "Individual")), TestUsers.Admin);

        Assert.Equal("Individual", updated["customer_type"]);
        Assert.Equal(100m, updated["credit_limit"]);
        Assert.Equal("Administrator", updated["modified_by"]);
        Assert.Equal("manager@example.com", updated["owner"]);
        Assert.True((DateTime)updated["modified"]! > (DateTime)doc["modified"]!);
    }

    [Fact]
    public async Task Update_with_stale_modified_is_a_conflict()
    {
        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme");
        var name = (string)doc["name"]!;
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));
        await _engine.Documents.UpdateAsync("Customer", name, Values(("credit_limit", 5)), TestUsers.Manager);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _engine.Documents.UpdateAsync(
                "Customer", name, Values(("credit_limit", 9), ("modified", doc["modified"])), TestUsers.Manager));
    }

    [Fact]
    public async Task Update_with_current_modified_succeeds()
    {
        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme");
        _engine.Clock.Advance(TimeSpan.FromMinutes(1));

        var updated = await _engine.Documents.UpdateAsync(
            "Customer", (string)doc["name"]!, Values(("credit_limit", 9), ("modified", doc["modified"])), TestUsers.Manager);

        Assert.Equal(9m, updated["credit_limit"]);
    }

    [Fact]
    public async Task Update_cannot_blank_a_required_field()
    {
        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme");

        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.UpdateAsync("Customer", (string)doc["name"]!, Values(("customer_name", "")), TestUsers.Manager));

        Assert.Contains(ex.Errors, e => e.Field == "customer_name");
    }

    [Fact]
    public async Task Delete_removes_the_document()
    {
        var doc = await InsertCustomerAsync(TestUsers.Manager, "Acme");
        var name = (string)doc["name"]!;

        await _engine.Documents.DeleteAsync("Customer", name, TestUsers.Manager);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Documents.GetAsync("Customer", name, TestUsers.Manager));
    }

    [Fact]
    public async Task List_supports_filters_ordering_paging_and_total()
    {
        foreach (var (name, limit) in new[] { ("Alpha", 10), ("Bravo", 20), ("Charlie", 30), ("Delta", 40) })
            await InsertCustomerAsync(TestUsers.Manager, name, ("credit_limit", limit));

        var page = await _engine.Documents.ListAsync("Customer", new ListRequest
        {
            Filters = [new RawFilter("credit_limit", ">=", 20)],
            OrderBy = "customer_name",
            Descending = false,
            Limit = 2,
            Offset = 1,
        }, TestUsers.Manager);

        Assert.Equal(["Charlie", "Delta"], page.Data.Select(r => (string)r["customer_name"]!));
        Assert.Equal(3, page.Total);
    }

    [Fact]
    public async Task List_supports_in_like_and_is_set_filters()
    {
        await InsertCustomerAsync(TestUsers.Manager, "Alpha Co", ("credit_limit", 1));
        await InsertCustomerAsync(TestUsers.Manager, "Beta Co");
        await InsertCustomerAsync(TestUsers.Manager, "Gamma Ltd", ("credit_limit", 3));

        async Task<string[]> Run(params RawFilter[] filters) =>
            (await _engine.Documents.ListAsync("Customer", new ListRequest { Filters = filters, OrderBy = "customer_name", Descending = false }, TestUsers.Manager))
            .Data.Select(r => (string)r["customer_name"]!).ToArray();

        Assert.Equal(["Alpha Co", "Beta Co"], await Run(new RawFilter("customer_name", "like", "%Co")));
        Assert.Equal(["Alpha Co", "Gamma Ltd"], await Run(new RawFilter("credit_limit", "is", "set")));
        Assert.Equal(["Beta Co"], await Run(new RawFilter("credit_limit", "is", "not set")));
        Assert.Equal(["Gamma Ltd"], await Run(new RawFilter("customer_name", "in", new object[] { "Gamma Ltd", "Nope" })));
    }

    [Fact]
    public async Task List_rejects_unknown_fields_and_operators()
    {
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() =>
            _engine.Documents.ListAsync("Customer", new ListRequest
            {
                Filters = [new RawFilter("name; DROP TABLE x", "=", 1), new RawFilter("customer_name", "~~", "x")],
                OrderBy = "nope",
                Fields = ["also_nope"],
            }, TestUsers.Manager));

        Assert.Equal(4, ex.Errors.Count);
    }

    [Fact]
    public async Task List_clamps_limit()
    {
        var result = await _engine.Documents.ListAsync(
            "Customer", new ListRequest { Limit = 1_000_000 }, TestUsers.Manager);

        Assert.Empty(result.Data);
    }

    [Fact]
    public void ListMetas_only_returns_doctypes_the_user_can_read()
    {
        Assert.Equal(["Customer", "Territory"], _engine.Documents.ListMetas(TestUsers.Manager).Select(m => m.Name));
        Assert.Equal(["Customer"], _engine.Documents.ListMetas(TestUsers.SalesA).Select(m => m.Name));
        Assert.Empty(_engine.Documents.ListMetas(UserContext.Anonymous));
    }
}
