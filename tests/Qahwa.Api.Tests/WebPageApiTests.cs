using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class WebPageApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Anyone_can_read_published_pages_and_the_index_and_nobody_can_read_a_draft()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Web Page", new { route = "hello", title = "Hello", published = true, content = "<p>Hi <b>there</b></p><script>x()</script>" });
        await admin.PostAsJsonAsync("/api/resource/Web Page", new { route = "draft", title = "Draft", published = false });
        var stranger = _factory.CreateClient(); // no cookie, no key

        var page = await stranger.GetAsync("/api/public/pages/hello");
        var index = await stranger.GetAsync("/api/public/pages");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = (await page.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("html").GetString();
        Assert.Contains("<b>there</b>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["hello"], (await index.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").EnumerateArray().Select(p => p.GetProperty("route").GetString()));
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("/api/public/pages/draft")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("/api/public/pages/nothing")).StatusCode);
    }
}
