using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Api;

namespace Qahwa.Api.Tests;

public sealed class OpenApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task MakeDocTypeAsync()
    {
        var made = await _factory.ClientFor("Administrator").PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Work Order", module = "Buying", autoname = "WO-.#####", isSubmittable = true,
                fields = new object[]
                {
                    new { name = "title", type = "Data", required = true, label = "Title </script><b>x</b>" },
                    new { name = "state", type = "Select", options = "Open\nDone" },
                    new { name = "cost", type = "Currency" },
                    new { name = "secret_note", type = "Text", permLevel = 1 },
                },
                permissions = new object[]
                {
                    new { role = "Reader", read = true },
                    new { role = "Editor", read = true, write = true, create = true, submit = true },
                },
            },
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
    }

    private async Task<JsonElement> SpecAsync(HttpClient client) =>
        JsonDocument.Parse(await client.GetStringAsync("/api/openapi.json")).RootElement.Clone();

    [Fact]
    public async Task The_description_lists_only_what_the_person_may_use()
    {
        await MakeDocTypeAsync();

        var reader = await SpecAsync(_factory.ClientFor("r@example.com", "Reader"));
        var readerPaths = reader.GetProperty("paths");
        Assert.True(readerPaths.TryGetProperty("/api/resource/Work%20Order", out var collection));
        Assert.True(collection.TryGetProperty("get", out _));
        Assert.False(collection.TryGetProperty("post", out _)); // may not create
        Assert.False(readerPaths.TryGetProperty("/api/resource/Work%20Order/{name}/submit", out _));
        Assert.False(reader.GetProperty("components").GetProperty("schemas").GetProperty("Work_Order").GetProperty("properties").TryGetProperty("secret_note", out _)); // a higher permission level

        var editor = await SpecAsync(_factory.ClientFor("e@example.com", "Editor"));
        var editorPaths = editor.GetProperty("paths");
        Assert.True(editorPaths.GetProperty("/api/resource/Work%20Order").TryGetProperty("post", out _));
        Assert.True(editorPaths.TryGetProperty("/api/resource/Work%20Order/{name}/submit", out _));
        Assert.False(editorPaths.GetProperty("/api/resource/Work%20Order/{name}").TryGetProperty("delete", out _));

        var nobody = await SpecAsync(_factory.ClientFor("n@example.com", "Nobody"));
        Assert.False(nobody.GetProperty("paths").TryGetProperty("/api/resource/Work%20Order", out _));
    }

    [Fact]
    public async Task Field_types_become_schemas_and_required_fields_are_listed_for_input()
    {
        await MakeDocTypeAsync();

        var schemas = (await SpecAsync(_factory.ClientFor("e@example.com", "Editor"))).GetProperty("components").GetProperty("schemas");

        var input = schemas.GetProperty("Work_OrderInput");
        Assert.Equal("title", input.GetProperty("required")[0].GetString());
        Assert.Equal("number", input.GetProperty("properties").GetProperty("cost").GetProperty("type").GetString());
        Assert.Contains("Done", input.GetProperty("properties").GetProperty("state").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
        Assert.False(input.GetProperty("properties").TryGetProperty("name", out _)); // standard fields are not sent
        Assert.True(schemas.GetProperty("Work_Order").GetProperty("properties").TryGetProperty("docstatus", out _));
    }

    [Fact]
    public async Task Names_are_made_safe_for_schema_keys_and_text_stays_data()
    {
        Assert.Equal("Sales_Order_2", OpenApiBuilder.SchemaName("Sales Order 2"));
        Assert.Equal("a_b_c", OpenApiBuilder.SchemaName("a/b\"c"));
        await MakeDocTypeAsync();

        var response = await _factory.ClientFor("e@example.com", "Editor").GetAsync("/api/openapi.json");

        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType); // served as JSON, never as a page
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("Title </script>", await response.Content.ReadAsStringAsync()); // labels stay data
    }

    [Fact]
    public async Task A_guest_gets_401_and_the_response_is_not_cached()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/openapi.json")).StatusCode);
        var ok = await _factory.ClientFor("e@example.com", "Editor").GetAsync("/api/openapi.json");
        Assert.Equal("no-store", ok.Headers.CacheControl!.ToString());
        Assert.Equal("application/json", ok.Content.Headers.ContentType!.MediaType);
    }
}
