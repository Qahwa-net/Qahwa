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

    [Fact]
    public async Task A_system_manager_can_run_a_job_by_hand_and_the_run_is_logged()
    {
        var admin = _factory.ClientFor("Administrator");

        var run = await admin.PostAsync("/api/scheduler/email%20queue/run", null);

        Assert.Equal(HttpStatusCode.OK, run.StatusCode);
        Assert.Equal("Nothing was due.", (await run.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("message").GetString());
        var overview = (await (await admin.GetAsync("/api/scheduler")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        var queue = overview.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("job").GetString() == "email queue");
        Assert.StartsWith("Run by hand: ", queue.GetProperty("last").GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Running_a_job_needs_a_system_manager_and_a_job_that_exists()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _factory.ClientFor("Administrator").PostAsync("/api/scheduler/no%20such%20job/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().PostAsync("/api/scheduler/email%20queue/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _factory.ClientFor("clerk@example.com", "Clerk").PostAsync("/api/scheduler/email%20queue/run", null)).StatusCode);
    }

    [Fact]
    public void The_jobs_the_page_describes_are_exactly_the_steps_the_scheduler_has()
    {
        var steps = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SchedulerService>(_factory.Services).StepNames;

        Assert.Equal(Qahwa.Core.SchedulerJobs.All.Select(j => j.Job).Order(StringComparer.Ordinal), steps.Order(StringComparer.Ordinal));
    }
}
