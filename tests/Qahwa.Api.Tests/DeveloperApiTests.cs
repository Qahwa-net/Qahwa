using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class DeveloperApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task Developer_mode_is_off_by_default_and_only_an_administrator_may_ask()
    {
        var admin = _factory.ClientFor("Administrator");

        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/developer")).GetProperty("data").GetProperty("enabled").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/developer/export-all", null)).StatusCode); // switched off
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("m@example.com", "System Manager").GetAsync("/api/developer")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).GetAsync("/api/developer")).StatusCode);
    }
}
