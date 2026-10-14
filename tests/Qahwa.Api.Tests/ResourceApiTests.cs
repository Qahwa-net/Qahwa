using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ResourceApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Manager => _factory.ClientFor("manager@example.com", "Sales Manager");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Full_crud_lifecycle_over_http()
    {
        var created = await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme", credit_limit = 50 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/resource/Customer/CUST-00001", created.Headers.Location?.ToString());
        var doc = (await JsonOf(created)).GetProperty("data");
        Assert.Equal("Acme", doc.GetProperty("customer_name").GetString());
        Assert.Equal("Company", doc.GetProperty("customer_type").GetString());

        var read = await Manager.GetAsync("/api/resource/Customer/CUST-00001");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        var updated = await Manager.PutAsJsonAsync("/api/resource/Customer/CUST-00001", new { credit_limit = 75 });
        Assert.Equal(75m, (await JsonOf(updated)).GetProperty("data").GetProperty("credit_limit").GetDecimal());

        var deleted = await Manager.DeleteAsync("/api/resource/Customer/CUST-00001");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Manager.GetAsync("/api/resource/Customer/CUST-00001")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_requests_get_401_with_the_error_envelope()
    {
        var response = await _factory.CreateClient().GetAsync("/api/resource/Customer");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", (await JsonOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Authenticated_user_without_the_role_gets_403()
    {
        var response = await _factory.ClientFor("nobody@example.com").GetAsync("/api/resource/Customer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("permission_denied", (await JsonOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Validation_errors_return_400_with_per_field_details()
    {
        var response = await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_type = "Government", bogus = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await JsonOf(response);
        Assert.Equal("validation_error", body.GetProperty("code").GetString());
        var fields = body.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("field").GetString()).ToList();
        Assert.Contains("customer_name", fields);
        Assert.Contains("customer_type", fields);
        Assert.Contains("bogus", fields);
    }

    [Fact]
    public async Task Malformed_body_returns_400_not_500()
    {
        var response = await Manager.PostAsync(
            "/api/resource/Customer", new StringContent("{ not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("bad_request", (await JsonOf(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Missing_doctype_and_document_return_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Manager.GetAsync("/api/resource/Nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Manager.GetAsync("/api/resource/Customer/missing")).StatusCode);
    }

    [Fact]
    public async Task Duplicate_unique_value_returns_409()
    {
        await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" });

        var response = await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Stale_modified_returns_409()
    {
        var created = await JsonOf(await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }));
        var modified = created.GetProperty("data").GetProperty("modified").GetString();
        await Task.Delay(20);
        await Manager.PutAsJsonAsync("/api/resource/Customer/CUST-00001", new { credit_limit = 1 });

        var response = await Manager.PutAsJsonAsync("/api/resource/Customer/CUST-00001", new { credit_limit = 2, modified });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task List_accepts_frappe_style_filters_fields_order_and_paging()
    {
        foreach (var (name, limit) in new[] { ("Alpha", 10), ("Bravo", 20), ("Charlie", 30) })
            await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = name, credit_limit = limit });

        var filters = Uri.EscapeDataString("""[["credit_limit", ">=", 20]]""");
        var fields = Uri.EscapeDataString("""["customer_name"]""");
        var response = await Manager.GetAsync(
            $"/api/resource/Customer?filters={filters}&fields={fields}&order_by=customer_name%20asc&limit_page_length=1&limit_start=1");

        var body = await JsonOf(response);
        Assert.Equal(2, body.GetProperty("total").GetInt32());
        var row = Assert.Single(body.GetProperty("data").EnumerateArray());
        Assert.Equal("Charlie", row.GetProperty("customer_name").GetString());
        Assert.False(row.TryGetProperty("credit_limit", out _));
    }

    [Fact]
    public async Task List_accepts_object_style_equality_filters()
    {
        await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Alpha" });
        await Manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Bravo" });

        var filters = Uri.EscapeDataString("""{"customer_name": "Bravo"}""");
        var body = await JsonOf(await Manager.GetAsync($"/api/resource/Customer?filters={filters}"));

        Assert.Equal(1, body.GetProperty("total").GetInt32());
    }

    [Theory]
    [InlineData("filters=not-json")]
    [InlineData("filters=%5B%5B%22only-one%22%5D%5D")]
    [InlineData("filters=%5B%5B%22nope%22%2C%22%3D%22%2C1%5D%5D")]
    [InlineData("order_by=nope")]
    public async Task Bad_list_parameters_return_400(string query)
    {
        var response = await Manager.GetAsync($"/api/resource/Customer?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Meta_endpoints_respect_permissions_and_serialize_enums_as_strings()
    {
        var anonymous = await JsonOf(await _factory.CreateClient().GetAsync("/api/meta"));
        Assert.Empty(anonymous.GetProperty("data").EnumerateArray());

        var names = (await JsonOf(await Manager.GetAsync("/api/meta"))).GetProperty("data")
            .EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Equal(["Customer", "Territory"], names);

        var meta = (await JsonOf(await Manager.GetAsync("/api/meta/Customer"))).GetProperty("data");
        var type = meta.GetProperty("fields").EnumerateArray()
            .First(f => f.GetProperty("name").GetString() == "credit_limit").GetProperty("type");
        Assert.Equal("Currency", type.GetString());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/meta/Customer")).StatusCode);
    }

}
