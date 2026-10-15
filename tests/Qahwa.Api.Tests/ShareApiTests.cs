using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ShareApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task Share_a_document_then_the_other_person_can_open_it_and_its_meta()
    {
        var admin = _factory.ClientFor("Administrator");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/resource/User", new { email = "guest@example.com", full_name = "Guest" })).StatusCode);
        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var guest = _factory.ClientFor("guest@example.com", "Sales Support");
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/resource/Customer/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/meta/Customer")).StatusCode);

        var put = await seller.PutAsJsonAsync($"/api/share/Customer/{id}", new { user = "guest@example.com", read = true, write = false });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("guest@example.com", (await JsonOf(put)).GetProperty("data")[0].GetProperty("user").GetString());

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync($"/api/resource/Customer/{id}")).StatusCode);
        var meta = await JsonOf(await guest.GetAsync("/api/meta/Customer"));
        Assert.True(meta.GetProperty("permissions").GetProperty("read").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/share/Customer/{id}")).StatusCode); // cannot manage shares

        var gone = await seller.PutAsJsonAsync($"/api/share/Customer/{id}", new { user = "guest@example.com", read = false, write = false });
        Assert.Equal(HttpStatusCode.OK, gone.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/resource/Customer/{id}")).StatusCode);
    }

    [Fact]
    public async Task Unknown_users_are_rejected_and_anonymous_callers_are_unauthorised()
    {
        var seller = _factory.ClientFor("a@example.com", "Sales User");
        var id = (await JsonOf(await seller.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        Assert.Equal(HttpStatusCode.BadRequest, (await seller.PutAsJsonAsync($"/api/share/Customer/{id}", new { user = "ghost@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/share/Customer/{id}")).StatusCode);
    }
}
