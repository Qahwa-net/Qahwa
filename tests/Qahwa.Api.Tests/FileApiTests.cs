using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class FileApiTests : IAsyncDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static async Task<string> CustomerAsync(HttpClient client) =>
        (await JsonOf(await client.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString()!;

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string id, string name, byte[] bytes, bool header = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/files/for/Customer/{id}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (header)
            request.Headers.Add("X-File-Name", Uri.EscapeDataString(name));
        return client.SendAsync(request);
    }

    [Fact]
    public async Task A_file_is_uploaded_listed_downloaded_safely_and_deleted()
    {
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = await CustomerAsync(ann);

        var created = await UploadAsync(ann, id, "logo.png", Png);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var file = (await JsonOf(created)).GetProperty("data");
        var fileId = file.GetProperty("id").GetString();
        Assert.Equal("image/png", file.GetProperty("contentType").GetString());

        var list = (await JsonOf(await ann.GetAsync($"/api/files/for/Customer/{id}"))).GetProperty("data");
        Assert.Equal(1, list.GetArrayLength());

        var download = await ann.GetAsync($"/api/files/{fileId}");
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("image/png", download.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("inline", download.Content.Headers.ContentDisposition!.ToString());
        Assert.Contains("nosniff", download.Headers.GetValues("X-Content-Type-Options"));
        Assert.Contains("sandbox", download.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", download.Headers.GetValues("Cache-Control").Single());

        Assert.Equal(HttpStatusCode.OK, (await ann.DeleteAsync($"/api/files/{fileId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync($"/api/files/{fileId}")).StatusCode);
    }

    [Fact]
    public async Task Anything_that_is_not_a_picture_downloads_as_an_attachment_with_a_safe_name()
    {
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = await CustomerAsync(ann);
        var html = Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");

        var fileId = (await JsonOf(await UploadAsync(ann, id, "../../évil\r\nSet-Cookie: x=1.html", html))).GetProperty("data").GetProperty("id").GetString();
        var download = await ann.GetAsync($"/api/files/{fileId}");

        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("attachment", download.Content.Headers.ContentDisposition!.ToString());
        Assert.DoesNotContain("\n", string.Join("", download.Content.Headers.GetValues("Content-Disposition")));
        Assert.False(download.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Callers_and_requests_are_checked()
    {
        var ann = _factory.ClientFor("a@example.com", "Sales Manager");
        var id = await CustomerAsync(ann);
        var fileId = (await JsonOf(await UploadAsync(ann, id, "a.txt", Encoding.UTF8.GetBytes("secret")))).GetProperty("data").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(_factory.CreateClient(), id, "a.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/files/{fileId}")).StatusCode);
        var outsider = _factory.ClientFor("o@example.com", "Outsider");
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/files/{fileId}")).StatusCode); // does not reveal that it exists
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(outsider, id, "a.txt", [1])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.DeleteAsync($"/api/files/{fileId}")).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(ann, id, "a.txt", [1], header: false)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(ann, id, "empty.txt", [])).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ann.GetAsync("/api/files/notanid")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(ann, "NOPE", "a.txt", [1])).StatusCode);
    }
}
