using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class PermissionApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");
    private HttpClient SysMgr => _factory.ClientFor("sysmgr@example.com", "System Manager");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    private static object Payroll() => new
    {
        definition = new
        {
            name = "Payroll",
            fields = new object[]
            {
                new { name = "who", type = "Data" },
                new { name = "pay", type = "Currency", permLevel = 1 },
            },
            permissions = new object[]
            {
                new { role = "Sales User", read = true, write = true, create = true },
                new { role = "Sales Manager", read = true, write = true, create = true },
                new { role = "Sales Manager", read = true, write = true, permLevel = 1 },
            },
        },
    };

    [Fact]
    public async Task Only_managers_can_use_the_permission_endpoints()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/permissions/doctypes")).StatusCode);
        var plain = _factory.ClientFor("a@example.com", "Sales User");
        Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync("/api/permissions/doctypes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SysMgr.GetAsync("/api/permissions/doctypes")).StatusCode);
    }

    [Fact]
    public async Task Customise_read_and_reset_round_trip()
    {
        var put = await SysMgr.PutAsJsonAsync("/api/permissions/doctypes/Territory", new
        {
            rules = new[] { new { role = "Sales User", read = true } },
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = (await JsonOf(put)).GetProperty("data");
        Assert.True(view.GetProperty("customized").GetBoolean());

        var user = _factory.ClientFor("a@example.com", "Sales User");
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/resource/Territory")).StatusCode);

        var get = await SysMgr.GetAsync("/api/permissions/doctypes/Territory");
        Assert.Equal("Sales User", (await JsonOf(get)).GetProperty("data").GetProperty("rules")[0].GetProperty("role").GetString());

        var reset = await SysMgr.DeleteAsync("/api/permissions/doctypes/Territory");
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/resource/Territory")).StatusCode);
    }

    [Fact]
    public async Task Invalid_rules_are_400_and_core_doctypes_are_administrator_only()
    {
        var bad = await SysMgr.PutAsJsonAsync("/api/permissions/doctypes/Territory", new
        {
            rules = new[] { new { role = "Sales User", read = true, create = true, permLevel = 1 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var core = await SysMgr.PutAsJsonAsync("/api/permissions/doctypes/User", new { rules = new[] { new { role = "X", read = true } } });
        Assert.Equal(HttpStatusCode.Forbidden, core.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SysMgr.GetAsync("/api/permissions/doctypes/Nope")).StatusCode);
    }

    [Fact]
    public async Task Field_levels_hide_fields_in_the_meta_and_in_documents()
    {
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/designer/doctypes", Payroll())).StatusCode);
        var id = (await JsonOf(await Admin.PostAsJsonAsync("/api/resource/Payroll", new { who = "A", pay = 10 }))).GetProperty("data").GetProperty("name").GetString();

        var user = _factory.ClientFor("a@example.com", "Sales User");
        var meta = await JsonOf(await user.GetAsync("/api/meta/Payroll"));
        var fields = meta.GetProperty("data").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ToList();
        Assert.DoesNotContain("pay", fields);
        Assert.DoesNotContain(1, meta.GetProperty("writableLevels").EnumerateArray().Select(l => l.GetInt32()));

        var doc = (await JsonOf(await user.GetAsync($"/api/resource/Payroll/{id}"))).GetProperty("data");
        Assert.False(doc.TryGetProperty("pay", out _));

        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var mmeta = await JsonOf(await manager.GetAsync("/api/meta/Payroll"));
        Assert.Contains("pay", mmeta.GetProperty("data").GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Contains(1, mmeta.GetProperty("writableLevels").EnumerateArray().Select(l => l.GetInt32()));

        var rejected = await user.PutAsJsonAsync($"/api/resource/Payroll/{id}", new { pay = 99 });
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }
}
