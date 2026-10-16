using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Put a deleted document back. The list of what was deleted is the ordinary list of the Deleted Document DocType.</summary>
public static class DeletedDocumentEndpoints
{
    public static IEndpointRouteBuilder MapQahwaDeletedDocuments(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/deleted-documents/{id}/restore", async (string id, HttpContext http, IUserContextResolver users, DeletedDocumentService deleted, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            var name = await deleted.RestoreAsync(id, user, ct);
            return Results.Ok(new { data = new { name } });
        });
        return app;
    }
}
