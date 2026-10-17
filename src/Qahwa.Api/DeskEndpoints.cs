using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;
public static class DeskEndpoints
{
    public static IEndpointRouteBuilder MapQahwaDesk(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/desk-settings");
        group.MapGet("", async (HttpContext http, IUserContextResolver users, DeskSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.GetAsync(await users.ResolveAsync(http, ct), ct) }));
        group.MapPut("", async (DeskSaveRequest body, HttpContext http, IUserContextResolver users, DeskSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.SaveAsync(body, await users.ResolveAsync(http, ct), ct) }));
        group.MapDelete("", async (HttpContext http, IUserContextResolver users, DeskSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.ResetAsync(await users.ResolveAsync(http, ct), ct) }));
        group.MapGet("/roles/{role}", async (string role, HttpContext http, IUserContextResolver users, DeskSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.RoleAsync(role, await users.ResolveAsync(http, ct), ct) }));
        group.MapPut("/roles/{role}", async (string role, DeskSaveRequest body, HttpContext http, IUserContextResolver users, DeskSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.SaveRoleAsync(role, body, await users.ResolveAsync(http, ct), ct) }));
        group.MapDelete("/roles/{role}", async (string role, HttpContext http, IUserContextResolver users, DeskSettingsService service, CancellationToken ct) =>
        { await service.ResetRoleAsync(role, await users.ResolveAsync(http, ct), ct); return Results.Ok(new { message = "ok" }); });
        return app;
    }
}
