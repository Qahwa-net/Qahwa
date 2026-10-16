using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Qahwa.Core;

namespace Qahwa.Api.Tests;

public sealed class ExcelApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private HttpClient Ann => _factory.ClientFor("a@example.com", "Sales Manager");

    private HttpClient Admin => _factory.ClientFor("Administrator");

    [Fact]
    public async Task A_list_is_exported_as_an_excel_file_that_reads_back_and_can_be_imported_again()
    {
        await Admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme, \"Sons\"", customer_type = "Company" });
        await Admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "مقهى الصباح", customer_type = "Individual" });

        var response = await Admin.GetAsync("/api/resource/Customer/export?format=xlsx");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Customer.xlsx", response.Content.Headers.ContentDisposition!.ToString());
        var rows = XlsxReader.Read(bytes);
        var header = rows[0];
        var nameColumn = header.IndexOf("customer_name");
        var names = rows.Skip(1).Select(r => r[nameColumn]).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["Acme, \"Sons\"", "مقهى الصباح"], names);

        // The same file imports into a clean system.
        await using var other = new QahwaFactory();
        var client = other.ClientFor("a@example.com", "Sales Manager");
        var done = (await (await client.PostAsJsonAsync("/api/import/Customer", new { xlsx = Convert.ToBase64String(bytes), mode = "Insert" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(2, done.GetProperty("created").GetInt32());
    }

    [Fact]
    public async Task Export_needs_the_export_right_and_other_formats_fall_back_to_csv()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("o@example.com", "Outsider").GetAsync("/api/resource/Customer/export?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/resource/Customer/export?format=xlsx")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Ann.GetAsync("/api/resource/Customer/export?format=xlsx")).StatusCode); // no export right
        var other = await Admin.GetAsync("/api/resource/Customer/export?format=pdf");
        Assert.Equal("text/csv", other.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task A_spreadsheet_is_checked_like_a_csv_and_bad_ones_are_a_clear_error()
    {
        var good = Convert.ToBase64String(XlsxWriter.Write(["customer_name", "customer_type"], [new Dictionary<string, object?> { ["customer_name"] = "Bolt", ["customer_type"] = "Company" }]));

        var preview = (await (await Ann.PostAsJsonAsync("/api/import/Customer/preview", new { xlsx = good, mode = "Insert" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(1, preview.GetProperty("rows").GetInt32());

        foreach (var bad in new[] { Convert.ToBase64String("not a spreadsheet"u8.ToArray()), "!!!not base64!!!", Convert.ToBase64String(Zip(("readme.txt", "x"))), new string('A', 15_000_000) })
        {
            var response = await Ann.PostAsJsonAsync("/api/import/Customer", new { xlsx = bad, mode = "Insert" });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        Assert.Equal(0, (await Ann.GetFromJsonAsync<JsonElement>("/api/resource/Customer?fields=%5B%22name%22%5D")).GetProperty("data").GetArrayLength());
    }

    private static byte[] Zip(params (string Name, string Content)[] parts)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in parts)
                using (var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false)))
                    w.Write(content);
        return output.ToArray();
    }
}
