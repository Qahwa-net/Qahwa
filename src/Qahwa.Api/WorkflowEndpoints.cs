using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <param name="Workflow">The complete workflow; it replaces the current one.</param>
/// <param name="Modified">The version the client loaded (null when the DocType had no workflow).</param>
public sealed record WorkflowSaveRequest(WorkflowDefinition Workflow, DateTime? Modified = null);

public sealed record WorkflowActionRequest(string? Action, DateTime? Modified = null);

/// <summary>Workflows: building them (System Managers) and using them on documents.</summary>
public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapQahwaWorkflows(this IEndpointRouteBuilder app)
    {
        var builder = app.MapGroup("/api/workflows");

        builder.MapGet("/doctypes", async (HttpContext http, IUserContextResolver users, WorkflowManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        builder.MapGet("/doctypes/{name}", async (string name, HttpContext http, IUserContextResolver users, WorkflowManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.GetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        builder.MapPut("/doctypes/{name}", async (
            string name, WorkflowSaveRequest body, HttpContext http, IUserContextResolver users, WorkflowManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.SaveAsync(name, body.Workflow, body.Modified, await users.ResolveAsync(http, ct), ct) }));

        // Creates the field that holds the state (or reuses it) and says which field it is.
        builder.MapPost("/doctypes/{name}/state-field", async (
            string name, HttpContext http, IUserContextResolver users, WorkflowStateFieldService fields, WorkflowManager manager, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            var field = await fields.EnsureAsync(name, user, ct);
            return Results.Ok(new { field, data = await manager.GetAsync(name, user, ct) });
        });

        builder.MapDelete("/doctypes/{name}", async (string name, HttpContext http, IUserContextResolver users, WorkflowManager manager, CancellationToken ct) =>
        {
            await manager.DeleteAsync(name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        // The page an emailed link opens: look at what it would do, then confirm it (as the signed-in person it was written for).
        var links = app.MapGroup("/api/workflow-links/{token}");
        links.MapGet("", async (string token, HttpContext http, IUserContextResolver users, WorkflowLinkService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.InfoAsync(token, await users.ResolveAsync(http, ct), ct) }));
        links.MapPost("/confirm", async (string token, HttpContext http, IUserContextResolver users, WorkflowLinkService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.ConfirmAsync(token, await users.ResolveAsync(http, ct), ct) }));

        // Using a workflow: where a document is and what I can do with it, doing it, and what is waiting for me.
        var doc = app.MapGroup("/api/resource/{doctype}/{name}");

        doc.MapGet("/workflow", async (
            string doctype, string name, HttpContext http, IUserContextResolver users, DocumentService docs, CancellationToken ct) =>
            Results.Ok(new { data = await docs.WorkflowStatusAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        doc.MapPost("/workflow", async (
            string doctype, string name, WorkflowActionRequest body, HttpContext http, IUserContextResolver users, DocumentService docs, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            await docs.ApplyWorkflowActionAsync(doctype, name, body.Action, body.Modified, user, ct);
            return Results.Ok(new { data = await docs.WorkflowStatusAsync(doctype, name, user, ct) });
        });

        app.MapGet("/api/workflow/pending", async (HttpContext http, IUserContextResolver users, DocumentService docs, CancellationToken ct) =>
            Results.Ok(new { data = await docs.PendingWorkflowAsync(await users.ResolveAsync(http, ct), ct) }));

        return app;
    }
}
