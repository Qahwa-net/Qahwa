using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <param name="Hooks">The complete set; it replaces the current one. A blank secret keeps the stored one.</param>
public sealed record WebhooksSaveRequest(WebhookSet Hooks, DateTime? Modified = null);

/// <summary>Webhooks: which other systems are told when documents change (System Managers).</summary>
public static class WebhookEndpoints
{
    public static IEndpointRouteBuilder MapQahwaWebhooks(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhooks");

        group.MapGet("/doctypes", async (HttpContext http, IUserContextResolver users, WebhookManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/doctypes/{name}", async (string name, HttpContext http, IUserContextResolver users, WebhookManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.GetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/doctypes/{name}", async (string name, WebhooksSaveRequest body, HttpContext http, IUserContextResolver users, WebhookManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.SaveAsync(name, body.Hooks, body.Modified, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/doctypes/{name}", async (string name, HttpContext http, IUserContextResolver users, WebhookManager manager, CancellationToken ct) =>
        {
            await manager.DeleteAsync(name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
