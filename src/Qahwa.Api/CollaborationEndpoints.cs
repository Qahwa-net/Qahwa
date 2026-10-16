using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <param name="User">The email of the person to assign to.</param>
/// <param name="Date">An optional due date, yyyy-MM-dd.</param>
public sealed record AssignRequest(string? User, string? Description = null, string? Date = null, string? Priority = null);

/// <param name="Tag">The tag to add.</param>
public sealed record TagRequest(string? Tag);

/// <summary>Assignments, following, notifications, tags and likes.</summary>
public static class CollaborationEndpoints
{
    private static DateTime? ParseBefore(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : throw new DocumentValidationException("'before' must be a date and time.");
    }

    public static IEndpointRouteBuilder MapQahwaCollaboration(this IEndpointRouteBuilder app)
    {
        var notes = app.MapGroup("/api/notifications");

        notes.MapGet("", async (
            bool? unread, int? limit, string? before, HttpContext http, IUserContextResolver users, NotificationService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.ListAsync(await users.ResolveAsync(http, ct), unread ?? false, limit ?? 30, ParseBefore(before), ct) }));

        notes.MapGet("/count", async (HttpContext http, IUserContextResolver users, NotificationService service, CancellationToken ct) =>
            Results.Ok(new { data = new { unread = await service.UnreadCountAsync(await users.ResolveAsync(http, ct), ct) } }));

        notes.MapPost("/read-all", async (HttpContext http, IUserContextResolver users, NotificationService service, CancellationToken ct) =>
        {
            await service.MarkAllReadAsync(await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        notes.MapPost("/{id}/read", async (string id, HttpContext http, IUserContextResolver users, NotificationService service, CancellationToken ct) =>
        {
            await service.MarkReadAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        var doc = app.MapGroup("/api/resource/{doctype}/{name}");

        doc.MapGet("/follow", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, FollowService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.StateAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        doc.MapPost("/follow", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, FollowService service, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            await service.FollowAsync(doctype, name, user, ct);
            return Results.Ok(new { data = await service.StateAsync(doctype, name, user, ct) });
        });

        doc.MapDelete("/follow", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, FollowService service, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            await service.UnfollowAsync(doctype, name, user, ct);
            return Results.Ok(new { data = await service.StateAsync(doctype, name, user, ct) });
        });

        doc.MapGet("/tags", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.TagsAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        doc.MapPost("/tags", async (
            string doctype, string name, TagRequest body, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.AddTagAsync(doctype, name, body.Tag, await users.ResolveAsync(http, ct), ct) }));

        doc.MapDelete("/tags/{tag}", async (
            string doctype, string name, string tag, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.RemoveTagAsync(doctype, name, tag, await users.ResolveAsync(http, ct), ct) }));

        app.MapGet("/api/tags/{doctype}", async (
            string doctype, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.PopularTagsAsync(doctype, await users.ResolveAsync(http, ct), ct) }));

        doc.MapGet("/like", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.LikeStateAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        doc.MapPost("/like", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.SetLikeAsync(doctype, name, true, await users.ResolveAsync(http, ct), ct) }));

        doc.MapDelete("/like", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, LabelService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.SetLikeAsync(doctype, name, false, await users.ResolveAsync(http, ct), ct) }));

        doc.MapGet("/assignments", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, AssignmentService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.ForDocumentAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        doc.MapPost("/assignments", async (
            string doctype, string name, AssignRequest body, HttpContext http, IUserContextResolver users, AssignmentService service, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            await service.AssignAsync(doctype, name, body.User, body.Description, body.Date, body.Priority, user, ct);
            return Results.Created($"/api/resource/{doctype}/{Uri.EscapeDataString(name)}/assignments", new { data = await service.ForDocumentAsync(doctype, name, user, ct) });
        });

        var todos = app.MapGroup("/api/assignments");

        todos.MapGet("/mine", async (
            string? status, HttpContext http, IUserContextResolver users, AssignmentService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.MineAsync(await users.ResolveAsync(http, ct), status ?? "Open", ct) }));

        todos.MapPost("/{id}/complete", async (
            string id, HttpContext http, IUserContextResolver users, AssignmentService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.CompleteAsync(id, await users.ResolveAsync(http, ct), ct) }));

        todos.MapDelete("/{id}", async (
            string id, HttpContext http, IUserContextResolver users, AssignmentService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.RemoveAsync(id, await users.ResolveAsync(http, ct), ct) }));

        return app;
    }
}
