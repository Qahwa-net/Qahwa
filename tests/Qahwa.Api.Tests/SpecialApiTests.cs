using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class SpecialApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task CreateDocTypeAsync(object definition) =>
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/designer/doctypes", new { definition })).StatusCode);

    [Fact]
    public void Csv_cells_are_quoted_and_cannot_run_as_formulas()
    {
        var csv = CsvExport.Write(
            ["name", "note", "amount"],
            [
                new Dictionary<string, object?> { ["name"] = "a,b", ["note"] = "=HYPERLINK(\"http://evil\")", ["amount"] = -5m },
                new Dictionary<string, object?> { ["name"] = "line\nbreak", ["note"] = "+1", ["amount"] = null },
                new Dictionary<string, object?> { ["name"] = "say \"hi\"", ["note"] = "@cmd", ["amount"] = 1.5m },
            ]);

        var lines = csv.Split("\r\n");
        Assert.Equal("name,note,amount", lines[0]);
        Assert.Equal("\"a,b\",\"'=HYPERLINK(\"\"http://evil\"\")\",-5", lines[1]); // text is defused, numbers are left alone
        Assert.StartsWith("\"line", lines[2]);
        Assert.Contains("'+1", csv);
        Assert.Contains("'@cmd", csv);
        Assert.Contains("\"say \"\"hi\"\"\"", csv);
    }

    [Fact]
    public async Task Export_downloads_csv_for_people_with_the_export_right_only()
    {
        await CreateDocTypeAsync(new
        {
            name = "Expense",
            fields = new object[] { new { name = "title", type = "Data" }, new { name = "amount", type = "Currency" } },
            permissions = new object[]
            {
                new { role = "Sales Manager", read = true, create = true, export = true, report = true },
                new { role = "Sales User", read = true },
            },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        await manager.PostAsJsonAsync("/api/resource/Expense", new { title = "=cmd|' /C calc'!A0", amount = 5 });

        var export = await manager.GetAsync("/api/resource/Expense/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        Assert.Contains("attachment", export.Content.Headers.ContentDisposition?.ToString());
        var body = await export.Content.ReadAsStringAsync();
        Assert.Contains("'=cmd", body);
        Assert.DoesNotContain(",=cmd", body);

        var user = _factory.ClientFor("u@example.com", "Sales User");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/resource/Expense/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/resource/Expense/export")).StatusCode);

        var report = await manager.GetAsync("/api/resource/Expense/report?group_by=title&aggregate=sum&field=amount");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.Equal(5m, (await JsonOf(report)).GetProperty("data")[0].GetProperty("value").GetDecimal());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/resource/Expense/report?group_by=title&aggregate=drop")).StatusCode);
    }

    [Fact]
    public async Task A_single_doctype_works_without_a_list_and_a_tree_serves_its_nodes()
    {
        await CreateDocTypeAsync(new
        {
            name = "Site Settings",
            isSingle = true,
            fields = new object[] { new { name = "title", type = "Data", @default = "My Site" } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");

        var shown = await JsonOf(await manager.GetAsync("/api/resource/Site%20Settings/Site%20Settings"));
        Assert.Equal("My Site", shown.GetProperty("data").GetProperty("title").GetString());
        var saved = await manager.PutAsJsonAsync("/api/resource/Site%20Settings/Site%20Settings", new { title = "Renamed" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.DeleteAsync("/api/resource/Site%20Settings/Site%20Settings")).StatusCode);

        await CreateDocTypeAsync(new
        {
            name = "Area",
            isTree = true,
            autoname = "field:area_name",
            fields = new object[] { new { name = "area_name", type = "Data", required = true } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
        });
        await manager.PostAsJsonAsync("/api/resource/Area", new { area_name = "Earth", is_group = true });
        await manager.PostAsJsonAsync("/api/resource/Area", new { area_name = "Asia", parent_node = "Earth" });

        var roots = await JsonOf(await manager.GetAsync("/api/tree/Area"));
        var kids = await JsonOf(await manager.GetAsync("/api/tree/Area?parent=Earth"));
        Assert.Equal("Earth", roots.GetProperty("data")[0].GetProperty("name").GetString());
        Assert.Equal("Asia", kids.GetProperty("data")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PostAsJsonAsync("/api/resource/Area", new { area_name = "Bad", parent_node = "Asia" })).StatusCode);
    }

    [Fact]
    public async Task Rename_over_the_api_and_the_series_manager()
    {
        await CreateDocTypeAsync(new
        {
            name = "Folder",
            allowRename = true,
            autoname = "FLD-.###",
            fields = new object[] { new { name = "title", type = "Data" } },
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true, delete = true } },
        });
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var id = (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Folder", new { title = "x" }))).GetProperty("data").GetProperty("name").GetString();

        var renamed = await manager.PostAsJsonAsync($"/api/resource/Folder/{id}/rename", new { newName = "Docs", merge = false });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Docs", (await JsonOf(renamed)).GetProperty("data").GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/resource/Folder/Docs")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var list = await JsonOf(await sysmgr.GetAsync("/api/naming/series"));
        Assert.Contains(list.GetProperty("data").EnumerateArray(), s => s.GetProperty("prefix").GetString() == "FLD-" && s.GetProperty("current").GetInt64() == 1);
        var set = await sysmgr.PutAsJsonAsync("/api/naming/series", new { prefix = "FLD-", current = 40 });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("FLD-041", (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Folder", new { title = "y" }))).GetProperty("data").GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/naming/series")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.PutAsJsonAsync("/api/naming/series", new { prefix = "NOPE-", current = 1 })).StatusCode);
    }
}
