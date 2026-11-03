using Qahwa.Api;
using Qahwa.Core;
using Qahwa.Core.Auth;

/// <summary>Site manager API: list, add and remove sites of a running server. Only the main site's administrators, only when switched on.</summary>
public static class SiteManagerEndpoints
{
    public static IEndpointRouteBuilder MapSiteManager(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sites");

        async Task<(SiteManager Sites, IUserContext User)> RequireAsync(HttpContext http, IUserContextResolver users, PermissionService permissions, CancellationToken ct)
        {
            // From an extra site (or when switched off) there is nothing here at all: a site must never administer its neighbours.
            // (an extra site's own services do not even hold the manager, which lives with the host)
            if (http.Items.ContainsKey(SiteMiddleware.ItemKey) || http.RequestServices.GetService<SiteManager>() is not { Enabled: true } sites)
                throw new NotFoundException("No such endpoint.");
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();
            if (RequestIdentity.IsApiKey(http))
                throw new PermissionDeniedException("Sites cannot be managed with an API key. Sign in to do that.");
            if (!permissions.IsAdministrator(user))
                throw new PermissionDeniedException("Only an Administrator can manage sites.");
            return (sites, user);
        }

        group.MapGet("", async (HttpContext http, IUserContextResolver users, PermissionService permissions, CancellationToken ct) =>
        {
            var (sites, _) = await RequireAsync(http, users, permissions, ct);
            return Results.Ok(new { data = sites.List() });
        });

        group.MapPost("", async (NewSiteRequest body, HttpContext http, IUserContextResolver users, PermissionService permissions, CancellationToken ct) =>
        {
            var (sites, _) = await RequireAsync(http, users, permissions, ct);
            var made = await sites.CreateAsync(body, ct);
            return Results.Created($"/api/sites/{made.Name}", new { data = made });
        });

        group.MapDelete("/{name}", async (string name, HttpContext http, IUserContextResolver users, PermissionService permissions, CancellationToken ct) =>
        {
            var (sites, _) = await RequireAsync(http, users, permissions, ct);
            await sites.RemoveAsync(name, ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
