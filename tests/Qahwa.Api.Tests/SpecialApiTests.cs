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

public sealed class LinkApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Link_search_and_titles_show_friendly_names()
    {
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var id = (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme Trading" }))).GetProperty("data").GetProperty("name").GetString();

        var search = await JsonOf(await manager.GetAsync("/api/link/Customer/search?q=acme"));
        var option = search.GetProperty("data")[0];
        Assert.Equal(id, option.GetProperty("value").GetString());
        Assert.Equal("Acme Trading", option.GetProperty("label").GetString());

        var names = Uri.EscapeDataString($"[\"{id}\",\"MISSING\"]");
        var titles = await JsonOf(await manager.GetAsync($"/api/link/Customer/titles?names={names}"));
        Assert.Equal("Acme Trading", titles.GetProperty("data").GetProperty(id!).GetProperty("label").GetString());
        Assert.False(titles.GetProperty("data").TryGetProperty("MISSING", out _));

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.GetAsync("/api/link/Customer/titles?names=not-json")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("n@example.com", "Nobody").GetAsync("/api/link/Customer/search?q=")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/link/Customer/search?q=")).StatusCode);
    }

    [Fact]
    public async Task The_share_picker_lists_people_by_name()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/User", new { email = "ann@example.com", full_name = "Ann Lee" });
        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var found = await JsonOf(await seller.GetAsync($"/api/share/Customer/{id}/users?q=ann"));
        Assert.Equal("Ann Lee", found.GetProperty("data")[0].GetProperty("label").GetString());

        await seller.PutAsJsonAsync($"/api/share/Customer/{id}", new { user = "ann@example.com", read = true });
        var shares = await JsonOf(await seller.GetAsync($"/api/share/Customer/{id}"));
        Assert.Equal("Ann Lee", shares.GetProperty("data")[0].GetProperty("fullName").GetString());

        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("b@example.com", "Sales User").GetAsync($"/api/share/Customer/{id}/users?q=a")).StatusCode);
    }
}

public sealed class CustomizeApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Add_a_custom_field_to_a_standard_doctype_over_the_api_and_use_it()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var put = await sysmgr.PutAsJsonAsync("/api/customize/doctypes/Customer", new
        {
            customization = new
            {
                customFields = new[] { new { name = "custom_phone", type = "Data", label = "Phone", required = false } },
                overrides = new { customer_type = new { label = "Kind" } },
            },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = (await JsonOf(put)).GetProperty("data");
        Assert.True(view.GetProperty("customized").GetBoolean());
        Assert.Contains(view.GetProperty("effective").GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "custom_phone");
        Assert.DoesNotContain(view.GetProperty("definition").GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "custom_phone");

        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var meta = await JsonOf(await manager.GetAsync("/api/meta/Customer"));
        Assert.Contains(meta.GetProperty("data").GetProperty("fields").EnumerateArray(), f => f.GetProperty("name").GetString() == "custom_phone");
        var created = await manager.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme", custom_phone = "555" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("555", (await JsonOf(created)).GetProperty("data").GetProperty("custom_phone").GetString());

        var reset = await sysmgr.DeleteAsync("/api/customize/doctypes/Customer");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.False((await JsonOf(reset)).GetProperty("data").GetProperty("customized").GetBoolean());
    }

    [Fact]
    public async Task The_customize_api_checks_who_asks_and_what_is_sent()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/customize/doctypes")).StatusCode);
        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/customize/doctypes")).StatusCode);

        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.GetAsync("/api/customize/doctypes")).StatusCode);
        var bad = await sysmgr.PutAsJsonAsync("/api/customize/doctypes/Customer", new { customization = new { customFields = new[] { new { name = "phone", type = "Data" } } } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await sysmgr.PutAsJsonAsync("/api/customize/doctypes/User", new { customization = new { customFields = Array.Empty<object>() } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sysmgr.GetAsync("/api/customize/doctypes/Nope")).StatusCode);
    }
}
