using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <param name="Rules">The complete rule set; it replaces the current one.</param>
/// <param name="Modified">The version the client loaded (null when the DocType had no rules).</param>
public sealed record AssignmentRulesSaveRequest(AssignmentRuleSet Rules, DateTime? Modified = null);

/// <summary>Assignment rules: who gets which documents (System Managers).</summary>
public static class AssignmentRuleEndpoints
{
    public static IEndpointRouteBuilder MapQahwaAssignmentRules(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/assignment-rules");

        group.MapGet("/doctypes", async (HttpContext http, IUserContextResolver users, AssignmentRuleManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/doctypes/{name}", async (string name, HttpContext http, IUserContextResolver users, AssignmentRuleManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.GetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/doctypes/{name}", async (
            string name, AssignmentRulesSaveRequest body, HttpContext http, IUserContextResolver users, AssignmentRuleManager manager, CancellationToken ct) =>
            Results.Ok(new { data = await manager.SaveAsync(name, body.Rules, body.Modified, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/doctypes/{name}", async (string name, HttpContext http, IUserContextResolver users, AssignmentRuleManager manager, CancellationToken ct) =>
        {
            await manager.DeleteAsync(name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
