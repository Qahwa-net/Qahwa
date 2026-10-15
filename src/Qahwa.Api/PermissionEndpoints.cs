using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <param name="Rules">The complete list of rules; it replaces the DocType's current rules.</param>
/// <param name="Modified">The version the client loaded (null when the DocType had no customised rules).</param>
public sealed record PermissionSaveRequest(List<DocPermMeta> Rules, DateTime? Modified = null);

public static class PermissionEndpoints
{
    public static IEndpointRouteBuilder MapQahwaPermissions(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/permissions");

        group.MapGet("/doctypes", async (
            HttpContext http, IUserContextResolver users, PermissionManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/doctypes/{name}", async (
            string name, HttpContext http, IUserContextResolver users, PermissionManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.GetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/doctypes/{name}", async (
            string name, PermissionSaveRequest body, HttpContext http, IUserContextResolver users, PermissionManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.SaveAsync(name, body.Rules, body.Modified, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/doctypes/{name}", async (
            string name, HttpContext http, IUserContextResolver users, PermissionManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ResetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        return app;
    }
}
