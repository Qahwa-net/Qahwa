using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ConversationApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task The_conversation_of_a_document_is_shown_to_people_who_can_read_it_only()
    {
        var admin = _factory.ClientFor("Administrator");
        var id = (await JsonOf(await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var mine = await admin.GetAsync($"/api/resource/Customer/{id}/conversation");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(0, (await JsonOf(mine)).GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/resource/Customer/{id}/conversation")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("nobody@example.com", "No Role").GetAsync($"/api/resource/Customer/{id}/conversation")).StatusCode);
    }
}
