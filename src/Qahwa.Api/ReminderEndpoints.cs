using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Reminders: be told about a document at a time you choose.</summary>
public static class ReminderEndpoints
{
    public static IEndpointRouteBuilder MapQahwaReminders(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resource/{doctype}/{name}/reminders", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, ReminderService reminders, CancellationToken ct) =>
            Results.Ok(new { data = await reminders.ForDocumentAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        app.MapPost("/api/resource/{doctype}/{name}/reminders", async (
            string doctype, string name, ReminderRequest body, HttpContext http, IUserContextResolver users, ReminderService reminders, CancellationToken ct) =>
        {
            var created = await reminders.CreateAsync(doctype, name, body, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/reminders/{created.Id}", new { data = created });
        });

        var group = app.MapGroup("/api/reminders");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, ReminderService reminders, CancellationToken ct) =>
            Results.Ok(new { data = await reminders.ListPendingAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, ReminderService reminders, CancellationToken ct) =>
        {
            await reminders.CancelAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
