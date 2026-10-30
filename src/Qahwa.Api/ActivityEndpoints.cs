using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record CommentRequest(string? Content);

/// <summary>A document's updates log and comments: <c>/api/resource/{doctype}/{name}/timeline</c> and <c>.../comments</c>.</summary>
public static class ActivityEndpoints
{
    private static DateTime? ParseBefore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value))
            throw new DocumentValidationException("'before' must be a date and time.");
        return value;
    }

    private static int ParseLimit(string? text) => int.TryParse(text, out var n) ? n : 50;

    public static IEndpointRouteBuilder MapQahwaActivity(this IEndpointRouteBuilder app)
    {
        // The linked document behind lookup(link_field, 'field'), for formulas the Desk works out while a form is edited.
        app.MapGet("/api/lookup/{doctype}/{name}", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, DocumentService documents, CancellationToken ct) =>
            Results.Ok(new { data = await documents.LookupAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        var group = app.MapGroup("/api/resource/{doctype}/{name}");

        group.MapGet("/timeline", async (
            string doctype, string name, string? limit, string? before, HttpContext http,
            IUserContextResolver users, ActivityService activity, CancellationToken ct) =>
            Results.Ok(new { data = await activity.TimelineAsync(doctype, name, await users.ResolveAsync(http, ct), ParseLimit(limit), ParseBefore(before), ct) }));

        group.MapGet("/comments", async (
            string doctype, string name, string? limit, string? before, HttpContext http,
            IUserContextResolver users, ActivityService activity, CancellationToken ct) =>
        {
            var items = await activity.TimelineAsync(doctype, name, await users.ResolveAsync(http, ct), ParseLimit(limit), ParseBefore(before), ct);
            return Results.Ok(new { data = items.Where(i => i.Kind == "comment") });
        });

        group.MapGet("/mention-users", async (
            string doctype, string name, string? q, HttpContext http,
            IUserContextResolver users, ActivityService activity, CancellationToken ct) =>
            Results.Ok(new { data = await activity.MentionCandidatesAsync(doctype, name, q, await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("/comments", async (
            string doctype, string name, CommentRequest body, HttpContext http,
            IUserContextResolver users, ActivityService activity, CancellationToken ct) =>
        {
            var item = await activity.AddCommentAsync(doctype, name, body.Content, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/resource/{doctype}/{Uri.EscapeDataString(name)}/comments/{item.Id}", new { data = item });
        });

        group.MapPut("/comments/{id}", async (
            string doctype, string name, string id, CommentRequest body, HttpContext http,
            IUserContextResolver users, ActivityService activity, CancellationToken ct) =>
            Results.Ok(new { data = await activity.UpdateCommentAsync(doctype, name, id, body.Content, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/comments/{id}", async (
            string doctype, string name, string id, HttpContext http,
            IUserContextResolver users, ActivityService activity, CancellationToken ct) =>
        {
            await activity.DeleteCommentAsync(doctype, name, id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
