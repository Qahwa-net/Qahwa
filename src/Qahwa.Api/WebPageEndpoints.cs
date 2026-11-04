using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Public pages, open to anyone: they never look at the cookie or an API key, give out only published pages, and are rate limited (ADR-160).</summary>
public static class WebPageEndpoints
{
    public static IEndpointRouteBuilder MapQahwaWebPages(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/pages").RequireRateLimiting(WebFormEndpoints.RateLimit);

        group.MapGet("", async (HttpContext http, WebPageService pages, CancellationToken ct) =>
        {
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { data = await pages.IndexAsync(ct) });
        });

        group.MapGet("/{route}", async (string route, HttpContext http, WebPageService pages, CancellationToken ct) =>
        {
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { data = await pages.GetAsync(route, ct) });
        });

        return app;
    }
}
