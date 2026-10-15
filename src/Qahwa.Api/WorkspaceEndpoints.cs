using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Workspaces: the pages people start from (everyone reads their own; System Managers arrange them).</summary>
public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapQahwaWorkspaces(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workspaces");

        group.MapGet("/mine", async (HttpContext http, IUserContextResolver users, WorkspaceService workspaces, CancellationToken ct) =>
            Results.Ok(new { data = await workspaces.MineAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("", async (HttpContext http, IUserContextResolver users, WorkspaceService workspaces, CancellationToken ct) =>
            Results.Ok(new { data = await workspaces.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("", async (WorkspaceRequest body, HttpContext http, IUserContextResolver users, WorkspaceService workspaces, CancellationToken ct) =>
        {
            var created = await workspaces.CreateAsync(body, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/workspaces/{created.Info.Id}", new { data = created });
        });

        group.MapGet("/{id}", async (string id, HttpContext http, IUserContextResolver users, WorkspaceService workspaces, CancellationToken ct) =>
            Results.Ok(new { data = await workspaces.GetAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/{id}", async (string id, WorkspaceRequest body, HttpContext http, IUserContextResolver users, WorkspaceService workspaces, CancellationToken ct) =>
            Results.Ok(new { data = await workspaces.UpdateAsync(id, body, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, WorkspaceService workspaces, CancellationToken ct) =>
        {
            await workspaces.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
