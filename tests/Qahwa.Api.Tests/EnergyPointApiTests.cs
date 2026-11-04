using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qahwa.Api.Tests;

public sealed class EnergyPointApiTests : IAsyncDisposable
{
    private readonly QahwaFactory _factory = new();

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task A_signed_in_person_sees_their_points_and_the_leaderboard_and_a_stranger_does_not()
    {
        var admin = _factory.ClientFor("Administrator");

        var mine = await admin.GetAsync("/api/energy-points");
        var board = await admin.GetAsync("/api/energy-points/leaderboard?period=week");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(0, (await mine.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/energy-points/leaderboard?period=decade")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/energy-points")).StatusCode);
    }
}
