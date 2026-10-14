using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ChildTableApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    private HttpClient Manager => _factory.ClientFor("manager@example.com", "Sales Manager");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    /// <summary>Designs "Order Line" (child) and "Order" (parent) through the designer API, like a user would.</summary>
    private async Task DesignOrderAsync()
    {
        var line = await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Order Line",
                isChildTable = true,
                fields = new object[]
                {
                    new { name = "item", type = "Data", required = true, inListView = true },
                    new { name = "qty", type = "Int", required = true, inListView = true },
                    new { name = "rate", type = "Currency" },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, line.StatusCode);

        var order = await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Order",
                autoname = "ORD-.####",
                fields = new object[]
                {
                    new { name = "title", type = "Data", required = true },
                    new { name = "lines", type = "Table", options = "Order Line", required = true },
                },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true, delete = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
    }

    [Fact]
    public async Task Rows_travel_inside_the_parent_document_as_json()
    {
        await DesignOrderAsync();

        var created = await Manager.PostAsJsonAsync("/api/resource/Order", new
        {
            title = "First",
            lines = new object[] { new { item = "Pen", qty = 2, rate = 1.5 }, new { item = "Book", qty = "5" } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = (await JsonOf(created)).GetProperty("data");
        var lines = doc.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Pen", "Book"], lines.Select(l => l.GetProperty("item").GetString()));
        Assert.Equal([1, 2], lines.Select(l => l.GetProperty("idx").GetInt32()));
        Assert.False(lines[0].TryGetProperty("parent", out _));

        var read = (await JsonOf(await Manager.GetAsync("/api/resource/Order/ORD-0001"))).GetProperty("data");
        Assert.Equal(2, read.GetProperty("lines").GetArrayLength());

        var firstRow = lines[0].GetProperty("name").GetString();
        var updated = await Manager.PutAsJsonAsync("/api/resource/Order/ORD-0001", new
        {
            lines = new object[] { new { name = firstRow, qty = 20 }, new { item = "Ink", qty = 1 } },
        });
        var after = (await JsonOf(updated)).GetProperty("data").GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["Pen", "Ink"], after.Select(l => l.GetProperty("item").GetString()));
        Assert.Equal(20, after[0].GetProperty("qty").GetInt32());
        Assert.Equal(firstRow, after[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Row_errors_come_back_with_paths_the_desk_can_place()
    {
        await DesignOrderAsync();

        var response = await Manager.PostAsJsonAsync("/api/resource/Order", new
        {
            title = "Bad",
            lines = new object[] { new { item = "Pen", qty = 1 }, new { qty = "many" } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var paths = (await JsonOf(response)).GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ToList();
        Assert.Contains("lines[1].item", paths);
        Assert.Contains("lines[1].qty", paths);
    }

    [Fact]
    public async Task The_meta_response_includes_the_child_definitions_for_the_grid()
    {
        await DesignOrderAsync();

        var body = await JsonOf(await Manager.GetAsync("/api/meta/Order"));

        var child = body.GetProperty("children").GetProperty("Order Line");
        Assert.True(child.GetProperty("isChildTable").GetBoolean());
        Assert.Equal(["item", "qty", "rate"], child.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Child_doctypes_are_not_exposed_as_resources_or_in_the_sidebar()
    {
        await DesignOrderAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync("/api/resource/Order Line")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.PostAsJsonAsync("/api/resource/Order Line", new { item = "x", qty = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync("/api/meta/Order Line")).StatusCode);

        var names = (await JsonOf(await Admin.GetAsync("/api/meta"))).GetProperty("data").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("Order Line", names);
    }

    [Fact]
    public async Task Deleting_an_order_removes_its_lines_and_frees_the_child_doctype()
    {
        await DesignOrderAsync();
        await Manager.PostAsJsonAsync("/api/resource/Order", new { title = "T", lines = new[] { new { item = "Pen", qty = 1 } } });

        Assert.Equal(HttpStatusCode.Conflict, (await Admin.DeleteAsync("/api/designer/doctypes/Order Line")).StatusCode); // still used
        Assert.Equal(HttpStatusCode.OK, (await Manager.DeleteAsync("/api/resource/Order/ORD-0001")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.DeleteAsync("/api/designer/doctypes/Order")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.DeleteAsync("/api/designer/doctypes/Order Line")).StatusCode);
    }

    [Fact]
    public async Task The_designer_lists_which_doctypes_are_child_tables()
    {
        await DesignOrderAsync();

        var items = (await JsonOf(await Admin.GetAsync("/api/designer/doctypes"))).GetProperty("data").EnumerateArray().ToList();

        Assert.True(items.Single(i => i.GetProperty("name").GetString() == "Order Line").GetProperty("isChildTable").GetBoolean());
        Assert.False(items.Single(i => i.GetProperty("name").GetString() == "Order").GetProperty("isChildTable").GetBoolean());
    }
}
