using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>How dates and weeks look: the display part of System Settings, for anyone signed in.</summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapQahwaSettings(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/settings", async (HttpContext http, IUserContextResolver users, SystemSettingsService settings, CancellationToken ct) =>
        {
            if (!(await users.ResolveAsync(http, ct)).IsAuthenticated)
                throw new UnauthenticatedException();
            return Results.Ok(new { data = await settings.GetAsync(ct) });
        });

        return app;
    }
}
