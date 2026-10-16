using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class BrandApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new(realAuth: true);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task The_sign_in_page_can_read_the_brand_without_signing_in_and_the_defaults_are_plain()
    {
        var response = await _factory.CreateClient().GetAsync("/api/brand");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Qahwa", data.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("logo").ValueKind);
        Assert.Equal("auto", data.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task A_manager_sets_the_brand_and_everyone_sees_it_but_only_valid_values_are_stored()
    {
        await using var header = new QahwaFactory();
        var manager = header.ClientFor("Administrator");

        var bad = await manager.PutAsJsonAsync("/api/resource/Brand Settings/Brand Settings", new { brand_name = "<img src=x onerror=alert(1)>" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var good = await manager.PutAsJsonAsync("/api/resource/Brand Settings/Brand Settings", new { brand_name = "Acme Coffee", brand_color = "#a11d33", default_theme = "Dark", login_message = "Welcome" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);

        var seen = (await header.CreateClient().GetFromJsonAsync<JsonElement>("/api/brand")).GetProperty("data");
        Assert.Equal("Acme Coffee", seen.GetProperty("name").GetString());
        Assert.Equal("#a11d33", seen.GetProperty("color").GetString());
        Assert.Equal("dark", seen.GetProperty("theme").GetString());

        var clerk = header.ClientFor("c@example.com", "Sales Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PutAsJsonAsync("/api/resource/Brand Settings/Brand Settings", new { brand_name = "Mine" })).StatusCode);
    }
}
