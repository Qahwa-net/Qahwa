using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Qahwa.Core;
using Qahwa.Testing;

namespace Qahwa.Api.Tests;

public class ErrorLogApiTests
{
    private static async Task<IHost> HostAsync(TestEngine engine, Func<Exception> fail) =>
        await new HostBuilder().ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services => { services.AddLogging(); services.AddSingleton(engine.ErrorLog); })
            .Configure(app =>
            {
                app.UseQahwaErrors();
                app.Run(_ => throw fail());
            })).StartAsync();

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> LogAsync(TestEngine engine) =>
        (await engine.Documents.ListAsync("Error Log", new ListRequest { Limit = 100 }, TestUsers.Admin)).Data;

    [Fact]
    public async Task An_unexpected_error_is_logged_for_managers_but_the_visitor_sees_nothing_useful()
    {
        var engine = new TestEngine();
        using var host = await HostAsync(engine, () => new InvalidOperationException("connection string password=hunter2 failed"));

        var response = await host.GetTestClient().PostAsync("/api/resource/Thing/abc?token=SECRET&x=1", new StringContent("""{ "card": "4111111111111111" }"""));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("InvalidOperation", body);

        var row = Assert.Single(await LogAsync(engine));
        Assert.Equal("Request", row["source"]);
        Assert.Equal("POST", row["method"]);
        Assert.Equal("/api/resource/Thing/abc", row["path"]); // no query string
        Assert.Equal("InvalidOperationException", row["error_type"]);
        Assert.Contains("hunter2", (string)row["message"]!); // for the manager who has to fix it
        Assert.DoesNotContain("SECRET", System.Text.Json.JsonSerializer.Serialize(row));
        Assert.DoesNotContain("4111", System.Text.Json.JsonSerializer.Serialize(row)); // the body is never kept
    }

    [Fact]
    public async Task Expected_errors_are_not_logged()
    {
        var engine = new TestEngine();
        using var host = await HostAsync(engine, () => new NotFoundException("nope"));

        Assert.Equal(HttpStatusCode.NotFound, (await host.GetTestClient().GetAsync("/x")).StatusCode);

        Assert.Empty(await LogAsync(engine));
    }

    [Fact]
    public async Task A_storm_of_errors_cannot_fill_the_log()
    {
        var engine = new TestEngine();
        using var host = await HostAsync(engine, () => new InvalidOperationException("boom"));
        var client = host.GetTestClient();

        for (var i = 0; i < ErrorLogService.MaxPerMinute + 20; i++)
            await client.GetAsync("/x");

        Assert.Equal(ErrorLogService.MaxPerMinute, (await LogAsync(engine)).Count);
    }

    [Fact]
    public async Task Only_a_system_manager_can_read_or_clear_the_log()
    {
        var engine = new TestEngine();
        await engine.ErrorLog.RecordAsync("Request", "GET", "/x", null, new InvalidOperationException("boom"));

        await Assert.ThrowsAsync<PermissionDeniedException>(() => engine.Documents.ListAsync("Error Log", new ListRequest { Limit = 5 }, new UserContext("ann@example.com", ["Clerk"])));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => engine.Documents.InsertAsync("Error Log", new Dictionary<string, object?> { ["source"] = "x" }, TestUsers.SystemManager));
        var name = (string)(await LogAsync(engine))[0]["name"]!;
        await engine.Documents.DeleteAsync("Error Log", name, TestUsers.SystemManager);
        Assert.Empty(await LogAsync(engine));
    }

    [Fact]
    public void Query_strings_and_fragments_are_cut_from_addresses()
    {
        Assert.Equal("/a/b", ErrorLogService.PathOnly("/a/b?token=1#x"));
        Assert.Equal("/a/b", ErrorLogService.PathOnly("/a/b#frag?x"));
        Assert.Null(ErrorLogService.PathOnly(null));
    }

    [Fact]
    public async Task Recording_never_throws_even_when_the_log_cannot_be_written()
    {
        var engine = new TestEngine();
        engine.Registry.Remove("Error Log");

        await engine.ErrorLog.RecordAsync("Request", "GET", "/x", null, new InvalidOperationException("boom")); // no exception
    }
}
