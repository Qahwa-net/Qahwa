using Qahwa.Api;
using Qahwa.Core;
using Qahwa.Core.Auth;

/// <summary>Runs one scheduler step now, from the Scheduled Jobs page (System Managers; ADR-155).</summary>
public static class SchedulerRunEndpoints
{
    public static IEndpointRouteBuilder MapSchedulerRun(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/scheduler/{job}/run", async (string job, HttpContext http, IUserContextResolver users, PermissionService permissions, SchedulerService scheduler, AccessLogService audit, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();
            if (RequestIdentity.IsApiKey(http))
                throw new PermissionDeniedException("Jobs cannot be run with an API key. Sign in to do that.");
            if (!(permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal)))
                throw new PermissionDeniedException("Only a System Manager can run a job.");
            var said = await scheduler.RunNowAsync(job, ct) ?? throw new NotFoundException($"There is no job called '{job}'.");
            await audit.RecordAsync("Run Job", "Scheduled Job", job, 1, user, ct);
            return Results.Ok(new { data = new { job, message = said } });
        });
        return app;
    }
}
