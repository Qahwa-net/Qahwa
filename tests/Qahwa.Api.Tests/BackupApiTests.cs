using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class BackupApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task<byte[]> DownloadAsync()
    {
        var response = await Admin.GetAsync("/api/backup/download");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("attachment", response.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        return await response.Content.ReadAsByteArrayAsync();
    }

    private static ByteArrayContent Zip(byte[] bytes, string type = "application/zip")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
        return content;
    }

    [Fact]
    public async Task An_administrator_downloads_a_zip_that_holds_a_manifest_and_the_documents()
    {
        await Admin.PostAsJsonAsync("/api/resource/Role", new { role_name = "Auditor" });

        var bytes = await DownloadAsync();

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("manifest.json"));
        Assert.NotNull(zip.GetEntry("bundle.json"));
        var manifest = JsonDocument.Parse(new StreamReader(zip.GetEntry("manifest.json")!.Open()).ReadToEnd()).RootElement;
        var role = manifest.GetProperty("tables").EnumerateArray().Single(t => t.GetProperty("docType").GetString() == "Role");
        Assert.Contains("Auditor", new StreamReader(zip.GetEntry(role.GetProperty("file").GetString()!)!.Open()).ReadToEnd());
    }

    [Fact]
    public async Task Nobody_else_can_download_or_restore_and_an_api_key_cannot_either()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("s@example.com", "System Manager").GetAsync("/api/backup/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("s@example.com", "System Manager").PostAsync("/api/backup/restore", Zip([1]))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/backup/download")).StatusCode);

        var admin = _factory.ClientFor("Administrator");
        var key = (await (await admin.PostAsJsonAsync("/api/api-keys", new { description = "ci" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("token").GetString();
        var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await bearer.GetAsync("/api/backup/download")).StatusCode); // a key is nobody here (no header user)
    }

    [Fact]
    public async Task A_preview_of_a_restore_says_what_would_happen_and_bad_uploads_are_refused()
    {
        var bytes = await DownloadAsync();

        var preview = await Admin.PostAsync("/api/backup/restore", Zip(bytes));
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var data = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.True(data.GetProperty("dryRun").GetBoolean()); // a preview unless asked otherwise

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Admin.PostAsync("/api/backup/restore", Zip(bytes, "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await Admin.PostAsync("/api/backup/restore", Zip(bytes, "application/json"))).StatusCode);
        var notZip = await Admin.PostAsync("/api/backup/restore?dryRun=false", Zip("definitely not a zip"u8.ToArray()));
        Assert.Equal(HttpStatusCode.BadRequest, notZip.StatusCode);
        Assert.Equal("validation_error", (await notZip.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Restoring_adds_missing_documents_through_the_api()
    {
        await Admin.PostAsJsonAsync("/api/resource/Role", new { role_name = "Auditor" });
        var bytes = await DownloadAsync();
        await Admin.DeleteAsync("/api/resource/Role/Auditor");
        Assert.Equal(HttpStatusCode.NotFound, (await Admin.GetAsync("/api/resource/Role/Auditor")).StatusCode);

        var restored = await Admin.PostAsync("/api/backup/restore?dryRun=false", Zip(bytes));

        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Admin.GetAsync("/api/resource/Role/Auditor")).StatusCode);
    }
}
