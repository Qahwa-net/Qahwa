using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Saved reports: made by anyone who can read the DocType, run for whoever opens them with their own permissions.</summary>
public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapQahwaReports(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reports");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
            Results.Ok(new { data = await reports.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapPost("", async (ReportRequest body, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
        {
            var created = await reports.CreateAsync(body, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/reports/{created.Id}", new { data = created });
        });

        // Runs a definition that has not been saved (the builder's preview). Checked and permissioned like a saved one.
        group.MapPost("/preview", async (ReportDefinition body, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
            Results.Ok(new { data = await reports.RunAsync(body, await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/{id}", async (string id, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
            Results.Ok(new { data = await reports.GetAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapPut("/{id}", async (string id, ReportRequest body, DateTime? modified, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
            Results.Ok(new { data = await reports.UpdateAsync(id, body, modified, await users.ResolveAsync(http, ct), ct) }));

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
        {
            await reports.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        group.MapGet("/{id}/run", async (string id, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
            Results.Ok(new { data = await reports.RunAsync(id, await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/{id}/csv", async (string id, HttpContext http, IUserContextResolver users, ReportService reports, CancellationToken ct) =>
        {
            var (columns, rows, truncated, title) = await reports.ExportAsync(id, await users.ResolveAsync(http, ct), ct);
            var fileName = new string(title.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            http.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{(fileName.Length == 0 ? "report" : fileName)}.csv\"";
            http.Response.Headers["X-Export-Truncated"] = truncated ? "true" : "false";
            return Results.Text(CsvExport.Write(columns, rows), "text/csv", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        });

        return app;
    }
}
