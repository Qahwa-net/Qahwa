using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class DocumentActionApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    private HttpClient Admin => _factory.ClientFor("Administrator");

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task An_action_made_through_the_api_runs_for_the_next_document_and_only_managers_can_make_one()
    {
        var made = await Admin.PostAsJsonAsync("/api/designer/doctypes", new
        {
            definition = new
            {
                name = "Job Card", module = "Buying", autoname = "JC-.#####",
                fields = new object[] { new { name = "title", type = "Data", required = true }, new { name = "stage", type = "Data" } },
                permissions = new[] { new { role = "Sales Manager", read = true, write = true, create = true } },
            },
        });
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);

        var clerk = _factory.ClientFor("c@example.com", "Sales Manager");
        var denied = await clerk.PostAsJsonAsync("/api/resource/Document Action", new { title = "x", reference_doctype = "Job Card", @event = "New", action = "Set Field", target_field = "stage", value = "Hacked" });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var action = await Admin.PostAsJsonAsync("/api/resource/Document Action", new { title = "Start", reference_doctype = "Job Card", @event = "New", action = "Set Field", target_field = "stage", value = "Started by {{user}}" });
        Assert.Equal(HttpStatusCode.Created, action.StatusCode);

        var card = await clerk.PostAsJsonAsync("/api/resource/Job Card", new { title = "Fix" });
        Assert.Equal(HttpStatusCode.Created, card.StatusCode);
        var name = JsonDocument.Parse(await card.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("name").GetString();

        var saved = JsonDocument.Parse(await clerk.GetStringAsync($"/api/resource/Job Card/{name}")).RootElement.GetProperty("data");
        Assert.Equal("Started by c@example.com", saved.GetProperty("stage").GetString());
    }
}
