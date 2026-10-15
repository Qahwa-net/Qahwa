using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class SubmittableApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private static async Task<JsonElement> JsonOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();

    [Fact]
    public async Task A_designer_made_submittable_doctype_can_be_submitted_and_cancelled_over_the_api()
    {
        var admin = _factory.ClientFor("Administrator");
        var created = await admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Voucher",
                isSubmittable = true,
                fields = new object[]
                {
                    new { name = "title", type = "Data", required = true },
                    new { name = "note", type = "Data", allowOnSubmit = true },
                },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true, submit = true, cancel = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var manager = _factory.ClientFor("m@example.com", "Sales Manager");
        var doc = (await JsonOf(await manager.PostAsJsonAsync("/api/resource/Voucher", new { title = "V1" }))).GetProperty("data");
        var id = doc.GetProperty("name").GetString();
        var meta = await JsonOf(await manager.GetAsync("/api/meta/Voucher"));
        Assert.True(meta.GetProperty("permissions").GetProperty("submit").GetBoolean());

        var submitted = await manager.PostAsync($"/api/resource/Voucher/{id}/submit", null);
        Assert.True(submitted.StatusCode == HttpStatusCode.OK, await submitted.Content.ReadAsStringAsync());
        Assert.Equal(1, (await JsonOf(submitted)).GetProperty("data").GetProperty("docstatus").GetInt32());

        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync($"/api/resource/Voucher/{id}", new { title = "changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.PutAsJsonAsync($"/api/resource/Voucher/{id}", new { note = "ok" })).StatusCode);

        var cancelled = await manager.PostAsJsonAsync($"/api/resource/Voucher/{id}/cancel", new { });
        Assert.Equal(2, (await JsonOf(cancelled)).GetProperty("data").GetProperty("docstatus").GetInt32());

        var nobody = _factory.ClientFor("n@example.com", "Nobody");
        Assert.Equal(HttpStatusCode.Forbidden, (await nobody.PostAsync($"/api/resource/Voucher/{id}/submit", null)).StatusCode);
    }

    [Fact]
    public async Task A_doctype_cannot_change_its_submittable_setting_after_it_exists()
    {
        var admin = _factory.ClientFor("Administrator");
        object Def(bool submittable) => new
        {
            definition = new { name = "Slip", isSubmittable = submittable, fields = new[] { new { name = "a", type = "Data" } }, permissions = Array.Empty<object>() },
        };
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/designer/doctypes", Def(false))).StatusCode);

        var update = await admin.PutAsJsonAsync("/api/designer/doctypes/Slip", Def(true));

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
    }
}
