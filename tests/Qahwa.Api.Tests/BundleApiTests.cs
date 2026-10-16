using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class BundleApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    private const string Sample = """
        { "format": "qahwa-bundle", "version": 1, "name": "x", "items": [
          { "kind": "Customization", "doctype": "Customer", "definition": { "customFields": [ { "name": "custom_vip", "type": "Check", "label": "VIP" } ] } } ] }
        """;

    [Fact]
    public async Task A_manager_exports_previews_and_imports_a_bundle()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");

        var preview = await sysmgr.PostAsync("/api/bundle/import", Json(Sample));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var report = JsonDocument.Parse(await preview.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.True(report.GetProperty("dryRun").GetBoolean()); // a preview unless asked otherwise
        Assert.Equal(1, report.GetProperty("applied").GetInt32());

        var applied = await sysmgr.PostAsync("/api/bundle/import?dryRun=false", Json(Sample));
        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.False(JsonDocument.Parse(await applied.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("dryRun").GetBoolean());

        var export = await sysmgr.GetAsync("/api/bundle/export?name=mine&kinds=Customization");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Contains("attachment", export.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("nosniff", export.Headers.GetValues("X-Content-Type-Options").Single());
        var text = await export.Content.ReadAsStringAsync();
        Assert.Contains("custom_vip", text);
        Assert.Contains("\"name\": \"mine\"", text);
    }

    [Fact]
    public async Task Others_cannot_export_or_import()
    {
        var clerk = _factory.ClientFor("c@example.com", "Clerk");

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/bundle/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsync("/api/bundle/import?dryRun=false", Json(Sample))).StatusCode);
    }

    [Fact]
    public async Task Bad_files_are_refused_with_a_reason()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");

        var wrongType = await sysmgr.PostAsync("/api/bundle/import", new StringContent(Sample, Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, wrongType.StatusCode);

        var notBundle = await sysmgr.PostAsync("/api/bundle/import", Json("""{ "hello": 1 }"""));
        Assert.Equal(HttpStatusCode.BadRequest, notBundle.StatusCode);

        var garbage = await sysmgr.PostAsync("/api/bundle/import", Json("{ not json"));
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        var huge = await sysmgr.PostAsync("/api/bundle/import", Json(new string(' ', 6 * 1024 * 1024)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, huge.StatusCode);

        var kind = await sysmgr.GetAsync("/api/bundle/export?kinds=Webhooks");
        Assert.Equal(HttpStatusCode.BadRequest, kind.StatusCode);
    }

    [Fact]
    public async Task A_hostile_definition_fails_on_its_own_and_changes_nothing()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var hostile = """
            { "format": "qahwa-bundle", "version": 1, "items": [
              { "kind": "DocType", "doctype": "Evil", "definition": { "name": "Evil", "fields": [ { "name": "x\"; drop table \"tabCustomer\"; --", "type": "Data" } ] } } ] }
            """;

        var response = await sysmgr.PostAsync("/api/bundle/import?dryRun=false", Json(hostile));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.Equal(1, report.GetProperty("failed").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor("Administrator").GetAsync("/api/resource/Customer")).StatusCode); // the table is still there
    }
}
