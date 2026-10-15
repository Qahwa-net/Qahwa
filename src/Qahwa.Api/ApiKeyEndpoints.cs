using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Manage your API keys. A key cannot be used to make or delete keys: only a signed-in person can.</summary>
public static class ApiKeyEndpoints
{
    public static IEndpointRouteBuilder MapQahwaApiKeys(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/api-keys");

        group.MapGet("", async (bool? all, HttpContext http, IUserContextResolver users, ApiKeyService keys, CancellationToken ct) =>
            Results.Ok(new { data = await keys.ListAsync(await users.ResolveAsync(http, ct), all ?? false, ct) }));

        group.MapPost("", async (ApiKeyRequest body, HttpContext http, IUserContextResolver users, ApiKeyService keys, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(http);
            var created = await keys.CreateAsync(body, user, ct);
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Created($"/api/api-keys/{created.Info.Id}", new { data = created });
        });

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, ApiKeyService keys, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            RequirePerson(http);
            await keys.RevokeAsync(id, user, ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }

    private static void RequirePerson(HttpContext http)
    {
        if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
            throw new PermissionDeniedException("API keys cannot be made or deleted with an API key. Sign in to do that.");
    }
}
