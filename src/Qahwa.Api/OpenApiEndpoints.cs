using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>The OpenAPI description of the API as the signed-in person (or API key) may use it.</summary>
public static class OpenApiEndpoints
{
    public static IEndpointRouteBuilder MapQahwaOpenApi(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/openapi.json", async (HttpContext http, DocumentService documents, IUserContextResolver users, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();

            var views = new List<MetaView>();
            foreach (var meta in await documents.ListMetasAsync(user, ct))
                views.Add(await documents.GetMetaViewAsync(meta.Name, user, ct));

            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Json(OpenApiBuilder.Build(views, "1"));
        });

        return app;
    }
}
