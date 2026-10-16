using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>
/// A stream of events (Server-Sent Events) for the signed-in person: new notifications, and, when a document is named, changes to that
/// document. The events say only that something happened; the Desk then asks for the data with its own, permission-checked requests.
/// </summary>
public static class RealtimeEndpoints
{
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(20);

    public static IEndpointRouteBuilder MapQahwaRealtime(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/realtime", async (
            string? doctype, string? name, HttpContext http, IUserContextResolver users, RealtimeHub hub, DocumentService documents, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();
            if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
                throw new PermissionDeniedException("Live updates are for signed-in sessions, not API keys.");
            if ((doctype is null) != (name is null))
                throw new DocumentValidationException("Give both the DocType and the name of the document, or neither.");
            if (doctype is not null)
                await documents.GetAsync(doctype, name!, user, ct); // you can only watch what you can read

            using var subscription = hub.Subscribe(user.User, doctype, name);
            if (subscription is null)
            {
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    wait.CancelAfter(KeepAlive);
                    try
                    {
                        var message = await subscription.Reader.ReadAsync(wait.Token);
                        var data = JsonSerializer.Serialize(new { doctype = message.DocType, name = message.Name, user = message.Type == "doc" ? message.User : null, deleted = message.Deleted ? true : (bool?)null },
                            new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                        await http.Response.WriteAsync($"event: {message.Type}\ndata: {data}\n\n", ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await http.Response.WriteAsync(": ping\n\n", ct); // keeps proxies from closing a quiet connection
                    }
                    catch (System.Threading.Channels.ChannelClosedException)
                    {
                        break;
                    }

                    await http.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException)
            {
                // the browser went away
            }
        });

        return app;
    }
}
