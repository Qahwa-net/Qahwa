using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class PatchApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_system_manager_can_see_the_patches_and_only_an_administrator_can_run_one()
    {
        var manager = _factory.ClientFor("m@example.com", "System Manager");
        var list = (await JsonOf(await manager.GetAsync("/api/patches"))).GetProperty("data");
        Assert.True(list.GetArrayLength() > 0);
        var name = list[0].GetProperty("name").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PostAsJsonAsync($"/api/patches/{name}/run", new { force = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("c@example.com", "Sales Manager").GetAsync("/api/patches")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/patches")).StatusCode);

        var admin = _factory.ClientFor("a@example.com", "Administrator");
        var ran = await admin.PostAsJsonAsync($"/api/patches/{name}/run", new { force = true });
        Assert.Equal(HttpStatusCode.OK, ran.StatusCode);
        Assert.Equal("Done", (await JsonOf(ran)).GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/patches/no-such-patch/run", new { force = true })).StatusCode);
    }
}
