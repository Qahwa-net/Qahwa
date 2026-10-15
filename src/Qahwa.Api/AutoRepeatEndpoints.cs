using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Auto Repeat: documents that make a copy of themselves on a schedule.</summary>
public static class AutoRepeatEndpoints
{
    public static IEndpointRouteBuilder MapQahwaAutoRepeat(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resource/{doctype}/{name}/auto-repeat", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, AutoRepeatService repeats, CancellationToken ct) =>
            Results.Ok(new { data = await repeats.ForDocumentAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        app.MapPost("/api/resource/{doctype}/{name}/auto-repeat", async (
            string doctype, string name, AutoRepeatRequest body, HttpContext http, IUserContextResolver users, AutoRepeatService repeats, CancellationToken ct) =>
        {
            var created = await repeats.CreateAsync(doctype, name, body, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/auto-repeat/{created.Id}", new { data = created });
        });

        var group = app.MapGroup("/api/auto-repeat");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, AutoRepeatService repeats, CancellationToken ct) =>
            Results.Ok(new { data = await repeats.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/{id}", async (string id, AutoRepeatRequest body, HttpContext http, IUserContextResolver users, AutoRepeatService repeats, CancellationToken ct) =>
            Results.Ok(new { data = await repeats.UpdateAsync(id, body, await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("/{id}/run", async (string id, HttpContext http, IUserContextResolver users, AutoRepeatService repeats, CancellationToken ct) =>
            Results.Ok(new { data = await repeats.RunNowAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, AutoRepeatService repeats, CancellationToken ct) =>
        {
            await repeats.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
