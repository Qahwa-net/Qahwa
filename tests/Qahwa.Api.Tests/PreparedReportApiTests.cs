using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Qahwa.Core;

namespace Qahwa.Api.Tests;

public sealed class PreparedReportApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private async Task PersonAsync(string email, string name, string role)
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Role", new { role_name = role });
        await admin.PostAsJsonAsync("/api/resource/User", new { email, full_name = name });
        await admin.PostAsJsonAsync("/api/resource/Has Role", new { user = email, role = role });
    }

    [Fact]
    public async Task A_report_is_prepared_listed_opened_and_deleted()
    {
        // The report is built as a real, enabled user (the synthetic Administrator of the test host has no user row).
        await PersonAsync("a@example.com", "Ann", "Administrator");
        var admin = _factory.ClientFor("a@example.com", "Administrator");
        await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" });
        var report = (await JsonOf(await admin.PostAsJsonAsync("/api/reports", new
        {
            title = "Customers", isPublic = false,
            definition = new { docType = "Customer", columns = new[] { "customer_name" }, limit = 50 },
        }))).GetProperty("data").GetProperty("id").GetString();

        var accepted = await admin.PostAsync($"/api/reports/{report}/prepare", null);
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var id = (await JsonOf(accepted)).GetProperty("data").GetProperty("id").GetString();

        await _factory.Services.GetRequiredService<PreparedReportService>().RunDueAsync();

        var listed = (await JsonOf(await admin.GetAsync($"/api/reports/{report}/prepared"))).GetProperty("data");
        Assert.Equal("Done", listed[0].GetProperty("status").GetString());
        var result = (await JsonOf(await admin.GetAsync($"/api/prepared-reports/{id}"))).GetProperty("data");
        Assert.Equal(1, result.GetProperty("rows").GetArrayLength());
        var header = result.GetProperty("columns")[0].GetString()!;
        Assert.Equal("Acme", result.GetProperty("rows")[0].GetProperty(header).GetString());

        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync($"/api/prepared-reports/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/prepared-reports/{id}")).StatusCode);
    }

    [Fact]
    public async Task Callers_are_checked()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync("/api/reports/x/prepare", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("Administrator").PostAsync("/api/reports/nope/prepare", null)).StatusCode);
    }
}
