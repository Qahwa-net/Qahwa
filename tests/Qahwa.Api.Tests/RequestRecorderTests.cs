using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public sealed class RequestRecorderTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();
    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public void It_keeps_the_slowest_requests_within_a_limit_and_cleans_the_path()
    {
        var recorder = new RequestRecorder();
        var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < RequestRecorder.Capacity + 50; i++)
            recorder.Record("GET", "/api/x/" + i, 200, 1000 + i, "a@example.com", at);
        recorder.Record("GET", "/api/evil\r\nSet-Cookie: x\u0007" + new string('p', 500), 200, 5, null, at);

        var slowest = recorder.Slowest(3);
        Assert.Equal(3, slowest.Count);
        Assert.True(slowest[0].Milliseconds >= slowest[1].Milliseconds);
        Assert.True(recorder.Slowest(1000).Count <= RequestRecorder.Capacity);
        Assert.All(recorder.Slowest(1000), r => Assert.True(r.Path.Length <= RequestRecorder.MaxPathLength && r.Path.All(c => !char.IsControl(c))));
        recorder.Clear();
        Assert.Empty(recorder.Slowest());
    }

    [Fact]
    public async Task Only_system_managers_read_the_slow_requests()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/system/slow-requests")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("u@example.com", "Sales User").GetAsync("/api/system/slow-requests")).StatusCode);
        var response = await _factory.ClientFor("Administrator").GetAsync("/api/system/slow-requests");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1000, body.GetProperty("thresholdMs").GetInt32());
        Assert.Equal(JsonValueKind.Array, body.GetProperty("data").ValueKind);
    }
}
