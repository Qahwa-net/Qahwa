using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Qahwa.Core;
using Qahwa.Core.Auth;

namespace Qahwa.Api;

/// <summary>What the scheduler does and how its jobs went lately (System Managers).</summary>
public static class SchedulerEndpoints
{
    public static IEndpointRouteBuilder MapQahwaScheduler(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/scheduler", async (HttpContext http, IUserContextResolver users, PermissionService permissions, JobLogService log, IConfiguration config, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();
            if (!(permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal)))
                throw new PermissionDeniedException("Only a System Manager can see the scheduler.");
            return Results.Ok(new
            {
                data = new
                {
                    enabled = config.GetValue("Qahwa:Scheduler:Enabled", true),
                    intervalSeconds = Math.Clamp(config.GetValue("Qahwa:Scheduler:IntervalSeconds", 300), 10, 3600),
                    jobs = await log.OverviewAsync(ct),
                    recent = await log.RecentAsync(30, null, ct),
                },
            });
        });
        return app;
    }
}
