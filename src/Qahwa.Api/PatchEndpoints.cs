using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record PatchRunRequest(bool Force = false);

/// <summary>The data patches of this server: which exist, which ran, and running one again. Patches change data, so only an Administrator may run one.</summary>
public static class PatchEndpoints
{
    public static IEndpointRouteBuilder MapQahwaPatches(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/patches");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, PermissionService permissions, PatchRunner patches, CancellationToken ct) =>
        {
            Require(await users.ResolveAsync(http, ct), permissions, manage: false);
            return Results.Ok(new { data = await patches.ListAsync(ct) });
        });

        group.MapPost("/{name}/run", async (string name, PatchRunRequest? body, HttpContext http, IUserContextResolver users, PermissionService permissions, PatchRunner patches, CancellationToken ct) =>
        {
            Require(await users.ResolveAsync(http, ct), permissions, manage: true);
            if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
                throw new PermissionDeniedException("Patches cannot be run with an API key. Sign in to do that.");
            try
            {
                return Results.Ok(new { data = await patches.RunOneAsync(name, body?.Force ?? false, ct) });
            }
            catch (PatchFailedException ex)
            {
                return Results.Json(new { code = "patch_failed", message = $"The patch '{name}' failed: {ex.InnerException?.Message}" }, statusCode: 500);
            }
        });

        return app;
    }

    private static void Require(IUserContext user, PermissionService permissions, bool manage)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var ok = manage ? permissions.IsAdministrator(user) : permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);
        if (!ok)
            throw new PermissionDeniedException(manage ? "Only an Administrator can run a patch." : "Only a System Manager can see the patches.");
    }
}
