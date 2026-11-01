using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Prepared reports: slow reports built in the background and kept for a week.</summary>
public static class PreparedReportEndpoints
{
    public static IEndpointRouteBuilder MapQahwaPreparedReports(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/reports/{id}/prepare", async (string id, HttpContext http, IUserContextResolver users, PreparedReportService prepared, CancellationToken ct) =>
        {
            var info = await prepared.RequestAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Accepted($"/api/prepared-reports/{info.Id}", new { data = info });
        });

        app.MapGet("/api/reports/{id}/prepared", async (string id, HttpContext http, IUserContextResolver users, PreparedReportService prepared, CancellationToken ct) =>
            Results.Ok(new { data = await prepared.ListAsync(id, await users.ResolveAsync(http, ct), ct) }));

        var group = app.MapGroup("/api/prepared-reports");

        group.MapGet("/{id}", async (string id, HttpContext http, IUserContextResolver users, PreparedReportService prepared, CancellationToken ct) =>
            Results.Ok(new { data = await prepared.GetAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, PreparedReportService prepared, CancellationToken ct) =>
        {
            await prepared.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
