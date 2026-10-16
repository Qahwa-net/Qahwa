using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class TranslationApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Translations_are_edited_by_a_manager_and_read_by_everyone_signed_in()
    {
        var sysmgr = _factory.ClientFor("s@example.com", "System Manager");
        var created = await sysmgr.PostAsJsonAsync("/api/resource/Translation", new { language = "ar", source_text = "Save", translated_text = "حفظ" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var clerk = _factory.ClientFor("c@example.com", "Clerk");
        var denied = await clerk.PostAsJsonAsync("/api/resource/Translation", new { language = "ar", source_text = "Open", translated_text = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var data = JsonDocument.Parse(await clerk.GetStringAsync("/api/translations/ar")).RootElement.GetProperty("data");
        Assert.True(data.GetProperty("rtl").GetBoolean());
        Assert.Equal("حفظ", data.GetProperty("messages").GetProperty("Save").GetString());
        Assert.Contains("ar", await clerk.GetStringAsync("/api/translations"));
    }

    [Fact]
    public async Task A_bad_language_code_is_refused_and_a_guest_is_not_served()
    {
        var clerk = _factory.ClientFor("c@example.com", "Clerk");

        Assert.Equal(HttpStatusCode.BadRequest, (await clerk.GetAsync("/api/translations/ar%27%3B%20drop")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/translations/ar")).StatusCode);
    }
}
