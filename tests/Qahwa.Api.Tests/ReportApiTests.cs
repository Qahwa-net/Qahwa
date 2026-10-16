using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ReportApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task SetUpAsync()
    {
        var made = await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Sale Line", module = "Buying", autoname = "SL-.#####",
                fields = new object[] { new { name = "region", type = "Data" }, new { name = "amount", type = "Currency" } },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true, report = true, @export = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var sales = _factory.ClientFor("s@example.com", "Sales Manager");
        foreach (var (region, amount) in new[] { ("North", 10), ("North", 20), ("South", 5) })
            Assert.Equal(HttpStatusCode.Created, (await sales.PostAsJsonAsync("/api/resource/Sale Line", new { region, amount })).StatusCode);
    }

    private static object Summary(string title = "By region", bool isPublic = true) => new
    {
        title, isPublic,
        definition = new { docType = "Sale Line", groupBy = "region", aggregates = new object[] { new { function = "count" }, new { function = "sum", field = "amount" } }, limit = 200 },
    };

    [Fact]
    public async Task A_report_is_saved_run_and_downloaded()
    {
        await SetUpAsync();
        var sales = _factory.ClientFor("s@example.com", "Sales Manager");

        var created = await sales.PostAsJsonAsync("/api/reports", Summary());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetString();

        var run = (await sales.GetFromJsonAsync<JsonElement>($"/api/reports/{id}/run")).GetProperty("data");
        Assert.True(run.GetProperty("grouped").GetBoolean());
        var north = run.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("region").GetString() == "North");
        Assert.Equal(2, north.GetProperty("count").GetDecimal());
        Assert.Equal(30, north.GetProperty("sum_amount").GetDecimal());

        var csv = await sales.GetAsync($"/api/reports/{id}/csv");
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        var xlsx = await sales.GetAsync($"/api/reports/{id}/xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        Assert.NotEmpty(Qahwa.Core.XlsxReader.Read(await xlsx.Content.ReadAsByteArrayAsync())[0]);
        Assert.Equal(HttpStatusCode.NotFound, (await sales.GetAsync($"/api/reports/{id}/pdf")).StatusCode);
        Assert.Contains("attachment", csv.Content.Headers.ContentDisposition!.ToString());
        var text = await csv.Content.ReadAsStringAsync();
        Assert.Contains("Total of amount", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("North,2,30", text);
    }

    [Fact]
    public async Task A_preview_runs_an_unsaved_definition_and_bad_ones_say_what_is_wrong()
    {
        await SetUpAsync();
        var sales = _factory.ClientFor("s@example.com", "Sales Manager");

        var preview = await sales.PostAsJsonAsync("/api/reports/preview", new { docType = "Sale Line", columns = new[] { "region", "amount" }, limit = 10 });
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(3, (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("rows").GetArrayLength());

        var bad = await sales.PostAsJsonAsync("/api/reports/preview", new { docType = "Sale Line", columns = new[] { "nope" }, limit = 10 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("columns", (await bad.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("details")[0].GetProperty("field").GetString());
    }

    [Fact]
    public async Task Sharing_never_widens_access_and_private_reports_stay_private()
    {
        await SetUpAsync();
        var sales = _factory.ClientFor("s@example.com", "Sales Manager");
        var open = (await (await sales.PostAsJsonAsync("/api/reports", Summary("Open", true))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetString();
        var mine = (await (await sales.PostAsJsonAsync("/api/reports", Summary("Mine", false))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("id").GetString();
        var outsider = _factory.ClientFor("o@example.com", "Outsider");

        Assert.Equal(HttpStatusCode.OK, (await outsider.GetAsync($"/api/reports/{open}")).StatusCode); // may open the definition
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/reports/{open}/run")).StatusCode); // but sees no data
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/reports/{open}/csv")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/reports/{mine}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.DeleteAsync($"/api/reports/{open}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/reports")).StatusCode);
    }
}
