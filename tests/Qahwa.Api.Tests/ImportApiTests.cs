using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ImportApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_file_is_checked_then_imported_and_its_rows_become_documents()
    {
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var csv = "customer_name,customer_type\r\nAcme,Individual\r\nBolt,Company\r\n,Individual\r\n";

        var preview = (await JsonOf(await ann.PostAsJsonAsync("/api/import/Customer/preview", new { csv, mode = "Insert" }))).GetProperty("data");
        Assert.Equal(3, preview.GetProperty("rows").GetInt32());
        Assert.Equal(1, preview.GetProperty("errors").GetArrayLength());
        Assert.Equal(0, (await JsonOf(await ann.GetAsync("/api/resource/Customer?fields=%5B%22name%22%5D"))).GetProperty("data").GetArrayLength());

        var done = (await JsonOf(await ann.PostAsJsonAsync("/api/import/Customer", new { csv, mode = "Insert" }))).GetProperty("data");
        Assert.Equal(2, done.GetProperty("created").GetInt32());
        Assert.Equal(1, done.GetProperty("failed").GetInt32());
        Assert.Equal(4, done.GetProperty("errors")[0].GetProperty("row").GetInt32()); // the third data row is line 4 of the file
        Assert.Equal(2, (await JsonOf(await ann.GetAsync("/api/resource/Customer?fields=%5B%22name%22%5D"))).GetProperty("data").GetArrayLength());

        var template = await ann.GetAsync("/api/import/Customer/template");
        Assert.Equal("text/csv", template.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("customer_name,", await template.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Callers_and_files_are_checked()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/import/Customer", new { csv = "customer_name\nX\n" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("o@example.com", "Outsider").PostAsJsonAsync("/api/import/Customer", new { csv = "customer_name\nX\n" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("a@example.com", "Sales Manager").PostAsJsonAsync("/api/import/Nope", new { csv = "a\n1\n" })).StatusCode);

        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/api/import/Customer", new { csv = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/api/import/Customer", new { csv = "customer_name\n\"unclosed\n" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await ann.PostAsJsonAsync("/api/import/Customer", new { csv = "customer_name\nX\n", mode = "Drop" })).StatusCode);
    }
}
