using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Reports emailed on a schedule (System Manager only).</summary>
public static class AutoEmailReportEndpoints
{
    public static IEndpointRouteBuilder MapQahwaAutoEmailReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auto-email-reports");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, AutoEmailReportService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("", async (AutoEmailReportRequest body, HttpContext http, IUserContextResolver users, AutoEmailReportService service, CancellationToken ct) =>
        {
            var info = await service.CreateAsync(body, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/auto-email-reports/{Uri.EscapeDataString(info.Id)}", new { data = info });
        });

        group.MapPut("/{id}", async (string id, AutoEmailReportRequest body, HttpContext http, IUserContextResolver users, AutoEmailReportService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.UpdateAsync(id, body, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, AutoEmailReportService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        group.MapPost("/{id}/send", async (string id, HttpContext http, IUserContextResolver users, AutoEmailReportService service, CancellationToken ct) =>
            Results.Ok(new { data = new { queued = await service.SendNowAsync(id, await users.ResolveAsync(http, ct), ct) } }));

        app.MapGet("/api/email-digest", async (HttpContext http, IUserContextResolver users, EmailDigestService digests, CancellationToken ct) =>
            Results.Ok(new { data = await digests.GetAsync(await users.ResolveAsync(http, ct), ct) }));

        app.MapPut("/api/email-digest", async (EmailDigestRequest body, HttpContext http, IUserContextResolver users, EmailDigestService digests, CancellationToken ct) =>
            Results.Ok(new { data = await digests.SetAsync(await users.ResolveAsync(http, ct), body, ct) }));

        return app;
    }
}
