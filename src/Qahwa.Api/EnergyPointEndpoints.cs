using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Energy points: my points and the leaderboard (ADR-159).</summary>
public static class EnergyPointEndpoints
{
    public static IEndpointRouteBuilder MapQahwaEnergyPoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/energy-points");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, EnergyPointService points, CancellationToken ct) =>
            Results.Ok(new { data = await points.MineAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/leaderboard", async (string? period, HttpContext http, IUserContextResolver users, EnergyPointService points, CancellationToken ct) =>
            Results.Ok(new { data = await points.LeaderboardAsync(period, await users.ResolveAsync(http, ct), ct) }));

        return app;
    }
}
