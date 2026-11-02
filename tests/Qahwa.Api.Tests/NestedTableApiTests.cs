using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class NestedTableApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task DesignAsync(HttpClient admin)
    {
        async Task Create(object definition) =>
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/designer/doctypes", new { definition })).StatusCode);

        await Create(new
        {
            name = "Kit Part", isChildTable = true,
            fields = new object[] { new { name = "part_name", type = "Data", required = true }, new { name = "qty", type = "Int" } },
        });
        await Create(new
        {
            name = "Kit Line", isChildTable = true,
            fields = new object[]
            {
                new { name = "title", type = "Data" },
                new { name = "parts", type = "Table", options = "Kit Part" },
                new { name = "part_count", type = "Int", formula = "sum(parts, 'qty')" },
            },
        });
        await Create(new
        {
            name = "Kit", autoname = "KIT-.####",
            fields = new object[] { new { name = "label", type = "Data" }, new { name = "lines", type = "Table", options = "Kit Line" } },
            permissions = new object[] { new { role = "System Manager", read = true, write = true, create = true, delete = true } },
        });
    }

    [Fact]
    public async Task A_table_inside_a_row_is_designed_saved_read_and_described_over_the_api()
    {
        var admin = _factory.ClientFor("Administrator");
        await DesignAsync(admin);

        var created = await admin.PostAsJsonAsync("/api/resource/Kit", new
        {
            label = "Frame",
            lines = new[] { new { title = "Side", parts = new[] { new { part_name = "bolt", qty = 4 }, new { part_name = "nut", qty = 6 } } } },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var doc = (await JsonOf(created)).GetProperty("data");
        var line = doc.GetProperty("lines")[0];
        Assert.Equal(10, line.GetProperty("part_count").GetInt32());
        Assert.Equal(2, line.GetProperty("parts").GetArrayLength());
        Assert.Equal("nut", line.GetProperty("parts")[1].GetProperty("part_name").GetString());

        // The Desk is told about the nested DocType too, so it can draw the inner grid.
        var meta = await JsonOf(await admin.GetAsync("/api/meta/Kit"));
        var children = meta.GetProperty("children");
        Assert.True(children.TryGetProperty("Kit Line", out _));
        Assert.True(children.TryGetProperty("Kit Part", out _));

        // A problem deep in the rows is reported with its full path.
        var bad = await admin.PostAsJsonAsync("/api/resource/Kit", new { label = "Bad", lines = new[] { new { title = "L", parts = new object[] { new { part_name = "ok" }, new { qty = 1 } } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("lines[0].parts[1].part_name", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_designer_refuses_tables_nested_deeper_than_one_level()
    {
        var admin = _factory.ClientFor("Administrator");
        await DesignAsync(admin);

        // Kit Line holds a table, so a table of Kit Lines inside another child would be three levels down.
        var deeper = await admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Kit Group", isChildTable = true,
                fields = new object[] { new { name = "lines", type = "Table", options = "Kit Line" } },
            },
        });

        Assert.Equal(HttpStatusCode.BadRequest, deeper.StatusCode);
        Assert.Contains("one level deep", await deeper.Content.ReadAsStringAsync());
    }
}
