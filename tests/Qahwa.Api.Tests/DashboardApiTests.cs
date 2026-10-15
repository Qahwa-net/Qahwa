using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class DashboardApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_dashboard_is_built_viewed_and_shared_over_the_api()
    {
        var ann = _factory.ClientFor("Administrator");
        foreach (var name in new[] { "Acme", "Bolt", "Cyan" })
            await ann.PostAsJsonAsync("/api/resource/Customer", new { customer_name = name, customer_type = name == "Bolt" ? "Company" : "Individual" });

        var created = await ann.PostAsJsonAsync("/api/dashboards", new
        {
            title = "Customers",
            isPublic = true,
            items = new object[]
            {
                new { type = "Number", title = "All", docType = "Customer", function = "count" },
                new { type = "Chart", title = "By type", docType = "Customer", groupBy = "customer_type", function = "count", chartType = "pie" },
                new { type = "List", title = "Latest", docType = "Customer", columns = new[] { "customer_name" }, limit = 2 },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await JsonOf(created)).GetProperty("data").GetProperty("id").GetString();

        var data = (await JsonOf(await ann.GetAsync($"/api/dashboards/{id}/data"))).GetProperty("data");
        Assert.Equal(3, data[0].GetProperty("number").GetDecimal());
        Assert.Equal("Individual", data[1].GetProperty("points")[0].GetProperty("label").GetString());
        Assert.Equal(2, data[2].GetProperty("rows").GetArrayLength());

        var bob = _factory.ClientFor("b@example.com", "Sales Manager");
        Assert.Equal(1, (await JsonOf(await bob.GetAsync("/api/dashboards"))).GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutAsJsonAsync($"/api/dashboards/{id}", new { title = "x", items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ann.DeleteAsync($"/api/dashboards/{id}")).StatusCode);
    }

    [Fact]
    public async Task Callers_and_input_are_checked()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/dashboards")).StatusCode);
        var ann = _factory.ClientFor("Administrator");
        var bad = await ann.PostAsJsonAsync("/api/dashboards", new { title = "T", items = new object[] { new { type = "Number", docType = "Customer", function = "sum", valueField = "customer_name" } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync("/api/dashboards/nope")).StatusCode);

        var preview = await ann.PostAsJsonAsync("/api/dashboards/preview", new { type = "Number", docType = "Customer", function = "count" });
        Assert.Equal(0, (await JsonOf(preview)).GetProperty("data").GetProperty("number").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/api/dashboards/preview", new { type = "Number", docType = "Customer", function = "count", filters = new object[] { new { field = "x'; DROP TABLE y; --", op = "=", value = 1 } } })).StatusCode);
    }
}
