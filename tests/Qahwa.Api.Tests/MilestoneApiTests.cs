using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class MilestoneApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_tracked_field_shows_its_milestones_over_the_api()
    {
        var admin = _factory.ClientFor("Administrator");
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/resource/Milestone Tracker", new { document_type = "Customer", track_field = "customer_type" })).StatusCode);
        var id = (await JsonOf(await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/resource/Customer/{id}", new { customer_type = "Individual" })).StatusCode);

        var view = (await JsonOf(await admin.GetAsync($"/api/resource/Customer/{id}/milestones"))).GetProperty("data");

        Assert.Equal("customer_type", view.GetProperty("field").GetString());
        var items = view.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("Company", items[0].GetProperty("value").GetString());
        Assert.Equal("Individual", items[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task A_bad_tracker_is_refused_and_an_untracked_doctype_has_none()
    {
        var admin = _factory.ClientFor("Administrator");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/resource/Milestone Tracker", new { document_type = "Customer", track_field = "nope" })).StatusCode);
        var id = (await JsonOf(await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" }))).GetProperty("data").GetProperty("name").GetString();

        var view = await JsonOf(await admin.GetAsync($"/api/resource/Customer/{id}/milestones"));
        Assert.Equal(JsonValueKind.Null, view.GetProperty("data").ValueKind);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"/api/resource/Customer/{id}/milestones")).StatusCode);
    }
}
