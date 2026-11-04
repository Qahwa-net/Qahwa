using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>The emails sent about a document and the replies matched to it (ADR-153).</summary>
public static class ConversationEndpoints
{
    public static IEndpointRouteBuilder MapQahwaConversation(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resource/{doctype}/{name}/conversation", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, ConversationService conversation, CancellationToken ct) =>
            Results.Ok(new { data = await conversation.ForDocumentAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        return app;
    }
}
