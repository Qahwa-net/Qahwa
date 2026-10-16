using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class MethodApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task An_app_method_is_called_by_name_with_post_or_get_and_the_installed_apps_are_listed()
    {
        var admin = _factory.ClientFor("Administrator");

        var post = (await (await admin.PostAsync("/api/method/testapp.hello", Json("""{ "who": "Qahwa" }"""))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Hello Qahwa", post.GetProperty("greeting").GetString());
        Assert.Equal("Administrator", post.GetProperty("user").GetString());

        var get = (await admin.GetFromJsonAsync<JsonElement>("/api/method/testapp.hello?who=Desk")).GetProperty("data");
        Assert.Equal("Hello Desk", get.GetProperty("greeting").GetString());

        var list = (await admin.GetFromJsonAsync<JsonElement>("/api/method")).GetProperty("data");
        Assert.Contains("testapp.hello", list.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
        Assert.Contains("qahwa.get_count", list.GetProperty("methods").EnumerateArray().Select(m => m.GetString()));
    }

    [Fact]
    public async Task Roles_methods_and_sign_in_are_enforced()
    {
        var clerk = _factory.ClientFor("c@example.com", "Sales Manager");

        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.PostAsync("/api/method/testapp.secret", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _factory.ClientFor("Administrator").PostAsync("/api/method/testapp.secret", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clerk.GetAsync("/api/method/testapp.secret")).StatusCode); // GET only when the method allows it
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.ClientFor(null).PostAsync("/api/method/testapp.hello", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.PostAsync("/api/method/testapp.nothing", Json("{}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.PostAsync("/api/method/..%2Fadmin", Json("{}"))).StatusCode);
    }

    [Fact]
    public async Task Odd_bodies_are_refused_and_big_ones_too()
    {
        var admin = _factory.ClientFor("Administrator");

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/method/testapp.hello", Json("{ not json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/method/testapp.hello", Json("[1,2]"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsync("/api/method/testapp.hello", Json("{\"who\":\"" + new string('x', 1_100_000) + "\"}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/method/testapp.hello", new ByteArrayContent([]))).StatusCode); // no body: no arguments
    }

    [Fact]
    public async Task The_built_in_functions_work_through_the_api_with_the_callers_rights()
    {
        var admin = _factory.ClientFor("Administrator");
        await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Acme" });
        await admin.PostAsJsonAsync("/api/resource/Customer", new { customer_name = "Bravo" });

        var count = (await (await admin.PostAsync("/api/method/qahwa.get_count", Json("""{ "doctype": "Customer" }"""))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal(2, count.GetInt32());
        var one = (await (await admin.PostAsync("/api/method/qahwa.get_value", Json("""{ "doctype": "Customer", "fieldname": "customer_name", "filters": [["customer_name", "like", "%rav%"]] }"""))).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.Equal("Bravo", one.GetString());
        var fromGet = (await admin.GetFromJsonAsync<JsonElement>("/api/method/qahwa.get_count?doctype=Customer")).GetProperty("data");
        Assert.Equal(2, fromGet.GetInt32());

        var nobody = _factory.ClientFor("n@example.com", "Nobody");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync("/api/method/qahwa.get_count", Json("""{ "doctype": "Customer" }"""))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync("/api/method/qahwa.set_value?doctype=Customer&name=x&fieldname=a&value=b")).StatusCode); // set_value is POST only
    }
}
