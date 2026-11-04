using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class SchedulerApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task System_managers_see_the_scheduler_and_everyone_else_does_not()
    {
        var response = await _factory.ClientFor("Administrator").GetAsync("/api/scheduler");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        Assert.False(data.GetProperty("enabled").GetBoolean()); // the test server turns the scheduler off
        Assert.True(data.GetProperty("intervalSeconds").GetInt32() >= 10);
        Assert.Contains(data.GetProperty("jobs").EnumerateArray(), j => j.GetProperty("job").GetString() == "email queue");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/scheduler")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("clerk@example.com", "Clerk").GetAsync("/api/scheduler")).StatusCode);
    }
}
