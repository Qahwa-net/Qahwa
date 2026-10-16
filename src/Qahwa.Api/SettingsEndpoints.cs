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

        // The sign-in page needs the name, logo and colours before anyone is signed in; they are public by nature.
        app.MapGet("/api/brand", async (IMetaRegistry registry, IDocumentRepository repository, CancellationToken ct) =>
        {
            var brand = await BrandService.GetAsync(registry, repository, ct);
            return Results.Ok(new { data = new { name = brand.Name, logo = brand.Logo, color = brand.Color, color_dark = brand.ColorDark, theme = brand.Theme, login_message = brand.LoginMessage } });
        });

        return app;
    }
}
