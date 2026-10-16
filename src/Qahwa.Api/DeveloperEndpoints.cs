using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;
using Qahwa.Core.Designer;

namespace Qahwa.Api;

/// <summary>Developer mode helpers (Administrator only, and only while <c>Qahwa:Developer:Enabled</c> is on).</summary>
public static class DeveloperEndpoints
{
    public static IEndpointRouteBuilder MapQahwaDeveloper(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/developer");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, DeveloperExport export, CancellationToken ct) =>
        {
            Require(await users.ResolveAsync(http, ct));
            return Results.Ok(new { data = new { enabled = export.Enabled } });
        });

        // Writes every DocType made in the Designer into the app folder (those made before developer mode was switched on).
        group.MapPost("/export-all", async (HttpContext http, IUserContextResolver users, DeveloperExport export, IDocTypeStore store, CancellationToken ct) =>
        {
            Require(await users.ResolveAsync(http, ct));
            if (!export.Enabled)
                throw new PermissionDeniedException("Developer mode is not switched on (Qahwa:Developer).");
            return Results.Ok(new { data = new { written = await export.WriteAllAsync(await store.ListAsync(ct), ct) } });
        });

        return app;
    }

    private static void Require(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!user.Roles.Contains(UserContext.AdministratorRole))
            throw new PermissionDeniedException("Only an Administrator can use developer mode.");
    }
}
