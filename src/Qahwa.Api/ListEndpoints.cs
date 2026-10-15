using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record SavedFilterRequest(string? Name, JsonElement Filters, bool IsPublic = false);

/// <param name="Action">"update" (set <paramref name="Field"/> to <paramref name="Value"/>) or "delete".</param>
public sealed record BulkRequest(string? Action, List<string>? Names, string? Field = null, JsonElement? Value = null);

/// <summary>List power: per-person list settings, saved filters, and bulk actions on chosen documents.</summary>
public static class ListEndpoints
{
    public static IEndpointRouteBuilder MapQahwaLists(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/list-settings/{doctype}");

        group.MapGet("", async (string doctype, HttpContext http, IUserContextResolver users, ListSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.GetAsync(doctype, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/settings", async (
            string doctype, ListViewSettings body, HttpContext http, IUserContextResolver users, ListSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.SaveSettingsAsync(doctype, body, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/settings", async (string doctype, HttpContext http, IUserContextResolver users, ListSettingsService service, CancellationToken ct) =>
        {
            await service.ResetSettingsAsync(doctype, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        group.MapPost("/filters", async (
            string doctype, SavedFilterRequest body, HttpContext http, IUserContextResolver users, ListSettingsService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.SaveFilterAsync(doctype, body.Name, body.Filters, body.IsPublic, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/filters/{id}", async (
            string doctype, string id, HttpContext http, IUserContextResolver users, ListSettingsService service, CancellationToken ct) =>
        {
            await service.DeleteFilterAsync(doctype, id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        app.MapPost("/api/resource/{doctype}/bulk", async (
            string doctype, BulkRequest body, HttpContext http, IUserContextResolver users, BulkService service, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            var results = body.Action switch
            {
                "update" => await service.UpdateAsync(doctype, body.Names, body.Field, body.Value is { } v ? v : null, user, ct),
                "delete" => await service.DeleteAsync(doctype, body.Names, user, ct),
                _ => throw new DocumentValidationException([new FieldError("action", "Use \"update\" or \"delete\".")]),
            };
            return Results.Ok(new { data = results, succeeded = results.Count(r => r.Ok), failed = results.Count(r => !r.Ok) });
        });

        return app;
    }
}
