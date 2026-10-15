using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record DashboardSaveRequest(string? Title = null, bool IsPublic = false, IReadOnlyList<DashboardItem>? Items = null, DateTime? Modified = null);

/// <summary>Dashboards: tiles of numbers, charts and lists, each shown with the viewer's own permissions.</summary>
public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapQahwaDashboards(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/dashboards");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
            Results.Ok(new { data = await dashboards.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("", async (DashboardSaveRequest body, HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
        {
            var created = await dashboards.CreateAsync(new DashboardRequest(body.Title, body.IsPublic, body.Items), await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/dashboards/{created.Id}", new { data = created });
        });

        group.MapPost("/preview", async (DashboardItem item, HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
            Results.Ok(new { data = await dashboards.PreviewAsync(item, await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/{id}", async (string id, HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
            Results.Ok(new { data = await dashboards.GetAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/{id}/data", async (string id, HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
            Results.Ok(new { data = await dashboards.DataAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/{id}", async (string id, DashboardSaveRequest body, HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
            Results.Ok(new { data = await dashboards.UpdateAsync(id, new DashboardRequest(body.Title, body.IsPublic, body.Items), body.Modified, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, DashboardService dashboards, CancellationToken ct) =>
        {
            await dashboards.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
