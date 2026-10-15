using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <param name="Customization">The complete customisation; it replaces the current one.</param>
/// <param name="Modified">The version the client loaded (null when the DocType had no customisation).</param>
public sealed record CustomizeSaveRequest(Customization Customization, DateTime? Modified = null);

public static class CustomizeEndpoints
{
    public static IEndpointRouteBuilder MapQahwaCustomize(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/customize");

        group.MapGet("/doctypes", async (
            HttpContext http, IUserContextResolver users, CustomizationManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/doctypes/{name}", async (
            string name, HttpContext http, IUserContextResolver users, CustomizationManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.GetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/doctypes/{name}", async (
            string name, CustomizeSaveRequest body, HttpContext http, IUserContextResolver users, CustomizationManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.SaveAsync(name, body.Customization ?? new Customization(), body.Modified, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/doctypes/{name}", async (
            string name, HttpContext http, IUserContextResolver users, CustomizationManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ResetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        return app;
    }
}
