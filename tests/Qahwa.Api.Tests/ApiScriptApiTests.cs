using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ApiScriptApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_signed_in_person_calls_an_API_script_over_http()
    {
        var admin = _factory.ClientFor("Administrator");
        var made = await admin.PostAsJsonAsync("/api/resource/Server%20Script", new { title = "Sum", @event = "API", method_name = "sum_two", script = "result = { total: doc.a + doc.b };" });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var user = _factory.ClientFor("u@example.com", "Sales User");
        var ok = await user.PostAsJsonAsync("/api/script-method/sum_two", new { a = 2, b = 3 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, body.GetProperty("data").GetProperty("total").GetInt32());

        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsJsonAsync("/api/script-method/sum_two", new { a = 1, b = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await user.PostAsJsonAsync("/api/script-method/missing", new { })).StatusCode);
        var bad = await user.PostAsync("/api/script-method/sum_two", new StringContent("[1,2]", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Only_system_managers_can_make_API_scripts()
    {
        var user = _factory.ClientFor("u@example.com", "Sales User");
        var response = await user.PostAsJsonAsync("/api/resource/Server%20Script", new { title = "x", @event = "API", method_name = "x", script = "result = 1;" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
