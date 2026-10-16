using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class DesignerApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static object Supplier(params object[] extraFields) => new
    {
        definition = new
        {
            name = "Supplier",
            module = "Buying",
            autoname = "SUP-.#####",
            titleField = "supplier_name",
            fields = new object[]
            {
                new { name = "supplier_name", type = "Data", label = "Supplier Name", required = true, inListView = true },
                new { name = "supplier_type", type = "Select", options = "Local\nInternational", @default = "Local" },
            }.Concat(extraFields).ToArray(),
            permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true, delete = true } },
        },
    };

    [Fact]
    public async Task Create_a_doctype_then_use_it_through_the_resource_api_and_the_sidebar()
    {
        var created = await Admin.PostAsJsonAsync("/api/designer/doctypes", Supplier());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var detail = (await JsonOf(created)).GetProperty("data");
        Assert.Equal("Custom", detail.GetProperty("source").GetString());
        Assert.True(detail.GetProperty("editable").GetBoolean());

        var manager = _factory.ClientFor("manager@example.com", "Sales Manager");
        var names = (await JsonOf(await manager.GetAsync("/api/meta"))).GetProperty("data")
            .EnumerateArray().Select(m => m.GetProperty("name").GetString()).ToList();
        Assert.Contains("Supplier", names);

        var doc = await manager.PostAsJsonAsync("/api/resource/Supplier", new { supplier_name = "Globex" });
        Assert.Equal(HttpStatusCode.Created, doc.StatusCode);
        Assert.Equal("SUP-00001", (await JsonOf(doc)).GetProperty("data").GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_field_type_catalog_marks_unsupported_types()
    {
        var types = (await JsonOf(await Admin.GetAsync("/api/designer/field-types"))).GetProperty("data").EnumerateArray().ToList();

        var currency = types.Single(t => t.GetProperty("type").GetString() == "Currency");
        var attach = types.Single(t => t.GetProperty("type").GetString() == "Password");
        Assert.True(currency.GetProperty("supported").GetBoolean());
        Assert.False(attach.GetProperty("supported").GetBoolean());
        Assert.True(types.Single(t => t.GetProperty("type").GetString() == "Table").GetProperty("supported").GetBoolean());
        Assert.Equal("DocType", types.Single(t => t.GetProperty("type").GetString() == "Link").GetProperty("options").GetString());
    }

    [Fact]
    public async Task Everyone_without_the_right_role_is_refused()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/designer/doctypes")).StatusCode);

        var manager = _factory.ClientFor("manager@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/designer/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync("/api/designer/doctypes", Supplier())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.GetAsync("/api/designer/field-types")).StatusCode);

        var sysmgr = _factory.ClientFor("sys@example.com", "System Manager");
        Assert.Equal(HttpStatusCode.OK, (await sysmgr.GetAsync("/api/designer/doctypes")).StatusCode);
    }

    [Fact]
    public async Task Invalid_definitions_return_400_with_a_path_for_each_problem()
    {
        var response = await Admin.PostAsJsonAsync("/api/designer/doctypes", Supplier(
            new { name = "Bad Name", type = "Data" }, new { name = "file", type = "Password" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await JsonOf(response)).GetProperty("details").EnumerateArray()
            .Select(d => d.GetProperty("field").GetString()).ToList();
        Assert.Contains("fields[2].name", fields);
        Assert.Contains("fields[3].type", fields);
    }

    [Fact]
    public async Task Preview_shows_the_database_changes_and_never_saves()
    {
        var preview = (await JsonOf(await Admin.PostAsJsonAsync("/api/designer/preview", Supplier()))).GetProperty("data");

        Assert.Empty(preview.GetProperty("errors").EnumerateArray());
        Assert.False(preview.GetProperty("schema").GetProperty("tableExists").GetBoolean());
        Assert.NotEmpty(preview.GetProperty("schema").GetProperty("changes").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync("/api/designer/doctypes/Supplier")).StatusCode);
    }

    [Fact]
    public async Task Edit_with_the_loaded_version_works_and_a_stale_version_conflicts()
    {
        var detail = (await JsonOf(await Admin.PostAsJsonAsync("/api/designer/doctypes", Supplier()))).GetProperty("data");
        var modified = detail.GetProperty("modified").GetString();
        await Task.Delay(20);

        var withRating = Supplier(new { name = "rating", type = "Int" });
        var body = new { definition = ((dynamic)withRating).definition, modified };
        var ok = await Admin.PutAsJsonAsync("/api/designer/doctypes/Supplier", body);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var stale = await Admin.PutAsJsonAsync("/api/designer/doctypes/Supplier", body);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Standard_doctypes_are_visible_but_read_only()
    {
        var list = (await JsonOf(await Admin.GetAsync("/api/designer/doctypes"))).GetProperty("data").EnumerateArray().ToList();
        Assert.False(list.Single(i => i.GetProperty("name").GetString() == "User").GetProperty("editable").GetBoolean());

        var detail = (await JsonOf(await Admin.GetAsync("/api/designer/doctypes/Customer"))).GetProperty("data");
        Assert.Equal("App", detail.GetProperty("source").GetString());
        Assert.False(detail.GetProperty("editable").GetBoolean());

        var edit = await Admin.PutAsJsonAsync("/api/designer/doctypes/Customer", new { definition = detail.GetProperty("definition") });
        Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);
    }

    [Fact]
    public async Task Delete_removes_an_empty_doctype_and_refuses_one_with_documents()
    {
        await Admin.PostAsJsonAsync("/api/designer/doctypes", Supplier());
        var manager = _factory.ClientFor("manager@example.com", "Sales Manager");
        await manager.PostAsJsonAsync("/api/resource/Supplier", new { supplier_name = "Globex" });

        Assert.Equal(HttpStatusCode.Conflict, (await Admin.DeleteAsync("/api/designer/doctypes/Supplier")).StatusCode);

        await manager.DeleteAsync("/api/resource/Supplier/SUP-00001");
        Assert.Equal(HttpStatusCode.OK, (await Admin.DeleteAsync("/api/designer/doctypes/Supplier")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync("/api/resource/Supplier")).StatusCode);
    }

    [Fact]
    public async Task Meta_includes_the_new_field_properties()
    {
        await Admin.PostAsJsonAsync("/api/designer/doctypes", Supplier(
            new { name = "note", type = "Text", description = "Internal note", hidden = true, readOnly = true }));

        var fields = (await JsonOf(await Admin.GetAsync("/api/meta/Supplier"))).GetProperty("data").GetProperty("fields")
            .EnumerateArray().ToList();
        var note = fields.Single(f => f.GetProperty("name").GetString() == "note");

        Assert.Equal("Internal note", note.GetProperty("description").GetString());
        Assert.True(note.GetProperty("hidden").GetBoolean());
        Assert.True(note.GetProperty("readOnly").GetBoolean());
    }
}
