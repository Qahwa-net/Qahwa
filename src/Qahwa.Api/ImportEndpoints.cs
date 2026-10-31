using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record ImportRequest(string? Csv = null, string? Mode = null, bool StopOnError = false, string? Xlsx = null, bool Background = false)
{
    /// <summary>The rows as CSV text: what was sent as CSV, or a spreadsheet (base64) turned into CSV so one import reads both.</summary>
    public string Text()
    {
        if (string.IsNullOrEmpty(Xlsx))
            return Csv ?? string.Empty;
        try
        {
            if (Xlsx.Length > XlsxReader.MaxFileBytes * 4 / 3 + 16)
                throw new FormatException($"The spreadsheet must be at most {XlsxReader.MaxFileBytes / 1024 / 1024} MB.");
            return XlsxReader.ToCsv(XlsxReader.Read(Convert.FromBase64String(Xlsx)));
        }
        catch (FormatException e)
        {
            throw new DocumentValidationException([new FieldError("file", e.Message)]);
        }
    }
}

/// <summary>Data import from CSV: a template, a check without importing, and the import itself.</summary>
public static class ImportEndpoints
{
    public static IEndpointRouteBuilder MapQahwaImport(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/import/{doctype}");

        group.MapGet("/template", async (string doctype, string? mode, HttpContext http, IUserContextResolver users, DataImportService import, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            var csv = import.Template(doctype, mode ?? "Insert", user);
            var safe = new string(doctype.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            http.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{(safe.Length == 0 ? "import" : safe)}-template.csv\"";
            return Results.Text(csv, "text/csv", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        });

        group.MapPost("/preview", async (string doctype, ImportRequest body, HttpContext http, IUserContextResolver users, DataImportService import, CancellationToken ct) =>
            Results.Ok(new { data = await import.PreviewAsync(doctype, body.Text(), body.Mode ?? "Insert", await users.ResolveAsync(http, ct), ct, body.Background) }));

        group.MapPost("", async (string doctype, ImportRequest body, HttpContext http, IUserContextResolver users, DataImportService import, CancellationToken ct) =>
            Results.Ok(new { data = await import.RunAsync(doctype, body.Text(), body.Mode ?? "Insert", body.StopOnError, await users.ResolveAsync(http, ct), ct) }));

        // A big file imported in the background: it is checked now, queued, and runs as the person while they carry on.
        group.MapPost("/background", async (string doctype, ImportRequest body, HttpContext http, IUserContextResolver users, ImportRunService runs, CancellationToken ct) =>
        {
            var started = await runs.StartAsync(doctype, body.Text(), body.Mode ?? "Insert", body.StopOnError, await users.ResolveAsync(http, ct), ct);
            _ = Task.Run(() => runs.RunDueAsync(CancellationToken.None)); // start at once; the scheduler would do it within minutes
            return Results.Accepted($"/api/import-runs/{started.Id}", new { data = started });
        });

        var runsGroup = app.MapGroup("/api/import-runs");
        runsGroup.MapGet("", async (bool? all, HttpContext http, IUserContextResolver users, ImportRunService runs, CancellationToken ct) =>
            Results.Ok(new { data = await runs.ListAsync(await users.ResolveAsync(http, ct), all ?? false, ct) }));
        runsGroup.MapGet("/{id}", async (string id, HttpContext http, IUserContextResolver users, ImportRunService runs, CancellationToken ct) =>
            Results.Ok(new { data = await runs.GetAsync(id, await users.ResolveAsync(http, ct), ct) }));
        runsGroup.MapPost("/{id}/cancel", async (string id, HttpContext http, IUserContextResolver users, ImportRunService runs, CancellationToken ct) =>
        {
            await runs.CancelAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
