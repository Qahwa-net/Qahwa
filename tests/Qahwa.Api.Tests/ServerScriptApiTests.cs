using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class ServerScriptApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    private async Task MakeJobCardAsync() =>
        Assert.Equal(HttpStatusCode.Created, (await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Job Card", module = "Buying", autoname = "JC-.#####",
                fields = new object[] { new { name = "title", type = "Data", required = true }, new { name = "stage", type = "Data" }, new { name = "hours", type = "Float" } },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
            },
        })).StatusCode);

    [Fact]
    public async Task A_script_made_through_the_api_checks_and_changes_what_people_save()
    {
        await MakeJobCardAsync();
        var clerk = _factory.ClientFor("c@example.com", "Sales Manager");
        var denied = await clerk.PostAsJsonAsync("/api/resource/Server Script", new { title = "x", reference_doctype = "Job Card", @event = "Validate", script = "doc.stage = 'Hacked';" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var made = await Admin.PostAsJsonAsync("/api/resource/Server Script", new
        {
            title = "Rules", reference_doctype = "Job Card", @event = "Validate",
            script = "if (doc.hours > 24) qahwa.error('A job cannot take more than 24 hours.'); doc.stage = doc.stage || 'Started by ' + user.name;",
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var refused = await clerk.PostAsJsonAsync("/api/resource/Job Card", new { title = "Fix", hours = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("24 hours", await refused.Content.ReadAsStringAsync());

        var ok = await clerk.PostAsJsonAsync("/api/resource/Job Card", new { title = "Fix", hours = 2 });
        var name = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("name").GetString();
        var saved = JsonDocument.Parse(await clerk.GetStringAsync($"/api/resource/Job Card/{name}")).RootElement.GetProperty("data");
        Assert.Equal("Started by c@example.com", saved.GetProperty("stage").GetString());
    }

    [Fact]
    public async Task A_script_with_a_mistake_is_refused_when_saved()
    {
        var bad = await Admin.PostAsJsonAsync("/api/resource/Server Script", new { title = "Bad", reference_doctype = "User", @event = "Validate", script = "if (" });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("mistake", await bad.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_script_can_be_tried_without_saving_anything_by_managers_only()
    {
        await MakeJobCardAsync();
        var request = new { doctype = "Job Card", @event = "Validate", script = "qahwa.log('hours', doc.hours); doc.stage = 'Tried';", doc = new { title = "T", hours = 3 } };

        var ok = await Admin.PostAsJsonAsync("/api/server-scripts/test", request);
        var data = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        Assert.Equal("Tried", data.GetProperty("changes").GetProperty("stage").GetString());
        Assert.Equal("hours 3", data.GetProperty("log")[0].GetString());

        var denied = await _factory.ClientFor("c@example.com", "Sales Manager").PostAsJsonAsync("/api/server-scripts/test", request);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var wrong = await Admin.PostAsJsonAsync("/api/server-scripts/test", new { doctype = "Job Card", @event = "Nope", script = "1;", doc = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        var runaway = await Admin.PostAsJsonAsync("/api/server-scripts/test", new { doctype = "Job Card", @event = "Validate", script = "while (true) {}", doc = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, runaway.StatusCode);
        var none = await _factory.CreateClient().PostAsJsonAsync("/api/server-scripts/test", request);
        Assert.NotEqual(HttpStatusCode.OK, none.StatusCode);
    }
}
