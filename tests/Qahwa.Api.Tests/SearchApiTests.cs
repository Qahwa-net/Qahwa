using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class SearchApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task The_search_box_finds_doctypes_and_documents_the_person_may_see_and_needs_a_sign_in()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Coffee Tasters" });

        var found = (await admin.GetFromJsonAsync<JsonElement>("/api/search?q=coffee")).GetProperty("data");
        Assert.Equal("Coffee Tasters", found.GetProperty("documents")[0].GetProperty("title").GetString());
        var types = (await admin.GetFromJsonAsync<JsonElement>("/api/search?q=custom")).GetProperty("data").GetProperty("doctypes");
        Assert.Contains(types.EnumerateArray(), t => t.GetProperty("name").GetString() == "Customer");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/search?q=coffee")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/search?q=" + new string('a', 101))).StatusCode);
        var empty = (await admin.GetFromJsonAsync<JsonElement>("/api/search")).GetProperty("data");
        Assert.Equal(0, empty.GetProperty("documents").GetArrayLength());
    }

    [Fact]
    public async Task Hostile_text_is_just_text()
    {
        var admin = _factory.ClientFor("Administrator");

        var response = await admin.GetAsync("/api/search?q=" + Uri.EscapeDataString("'; DROP TABLE tabRole; --"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/resource/Role")).StatusCode);
    }
}
