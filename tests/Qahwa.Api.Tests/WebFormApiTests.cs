using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class WebFormApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task SetUpAsync(int perHour = 10)
    {
        var made = await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Lead Note", module = "Buying", autoname = "LN-.#####",
                fields = new object[] { new { name = "who", type = "Data", required = true }, new { name = "what", type = "Text" }, new { name = "note_secret", type = "Data", permLevel = 1 } },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var form = await Admin.PostAsJsonAsync("/api/resource/Web Form", new { route = "hello", title = "Hello", published = 1, doc_type = "Lead Note", fields = "who\nwhat", max_per_hour = perHour });
        Assert.Equal(HttpStatusCode.Created, form.StatusCode);
    }

    private static StringContent Json(string text) => new(text, Encoding.UTF8, "application/json");

    [Fact]
    public async Task A_guest_opens_and_sends_a_form_and_gets_nothing_but_a_message()
    {
        await SetUpAsync();
        var guest = _factory.ClientFor(null);

        var form = JsonDocument.Parse(await guest.GetStringAsync("/api/public/forms/hello")).RootElement.GetProperty("data");
        Assert.Equal("Hello", form.GetProperty("title").GetString());
        Assert.Equal(2, form.GetProperty("fields").GetArrayLength());

        var sent = await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": "Sam", "what": "Hi <b>there</b>" }"""));
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        var text = await sent.Content.ReadAsStringAsync();
        Assert.DoesNotContain("LN-", text);
        Assert.Equal("no-store", sent.Headers.CacheControl!.ToString());

        var list = JsonDocument.Parse(await Admin.GetStringAsync("/api/resource/Lead Note")).RootElement.GetProperty("data");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("web-form@guest", list[0].GetProperty("owner").GetString());
    }

    [Fact]
    public async Task Unknown_unpublished_and_unsafe_forms_are_404_and_extra_fields_are_400()
    {
        await SetUpAsync();
        var guest = _factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/api/public/forms/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/api/public/forms/Hello")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": "Sam", "owner": "Administrator" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": "Sam", "note_secret": "x" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": { "a": 1 } }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "what": "no name" }"""))).StatusCode);

        await Admin.PutAsJsonAsync("/api/resource/Web Form/hello", new { published = 0 });
        Assert.Equal(HttpStatusCode.NotFound, (await guest.GetAsync("/api/public/forms/hello")).StatusCode);
    }

    [Fact]
    public async Task Bad_bodies_are_refused()
    {
        await SetUpAsync();
        var guest = _factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await guest.PostAsync("/api/public/forms/hello", new StringContent("""{ "who": "x" }""", Encoding.UTF8, "text/plain"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/public/forms/hello", Json("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await guest.PostAsync("/api/public/forms/hello", Json("[1,2]"))).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, (await guest.PostAsync("/api/public/forms/hello", Json($$"""{ "who": "{{new string('a', 40_000)}}" }"""))).StatusCode);
    }

    [Fact]
    public async Task Flooding_is_stopped_with_429()
    {
        await SetUpAsync(perHour: 2);
        var guest = _factory.ClientFor(null);

        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": "a" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": "b" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await guest.PostAsync("/api/public/forms/hello", Json("""{ "who": "c" }"""))).StatusCode);
    }

    [Fact]
    public async Task A_signed_in_visit_gets_no_extra_powers()
    {
        await SetUpAsync();
        var admin = _factory.ClientFor("Administrator");

        await admin.PostAsync("/api/public/forms/hello", Json("""{ "who": "Admin" }"""));

        var list = JsonDocument.Parse(await Admin.GetStringAsync("/api/resource/Lead Note")).RootElement.GetProperty("data");
        Assert.Equal("web-form@guest", list[0].GetProperty("owner").GetString()); // the cookie or header is never used here
    }
}
