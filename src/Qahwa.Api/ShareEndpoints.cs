using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record ShareRequest(string? User, bool Read = true, bool Write = false);

public static class ShareEndpoints
{
    public static IEndpointRouteBuilder MapQahwaSharing(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/share/{doctype}/{name}");

        group.MapGet("", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, ShareManager shares, CancellationToken ct) =>
            Results.Ok(new { data = await shares.ListAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("", async (
            string doctype, string name, ShareRequest body, HttpContext http, IUserContextResolver users, ShareManager shares, CancellationToken ct) =>
        {
            var actor = await users.ResolveAsync(http, ct);
            await shares.SetAsync(doctype, name, body.User, body.Read, body.Write, actor, ct);
            return Results.Ok(new { data = await shares.ListAsync(doctype, name, actor, ct) });
        });

        return app;
    }
}
