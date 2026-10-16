using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Export and import of what was built in the Desk, as one file (System Managers).</summary>
public static class BundleEndpoints
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static IEndpointRouteBuilder MapQahwaBundle(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/bundle");

        group.MapGet("/export", async (string? name, string? kinds, HttpContext http, IUserContextResolver users, BundleService service, CancellationToken ct) =>
        {
            var wanted = new List<BundleKind>();
            foreach (var part in (kinds ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!Enum.TryParse<BundleKind>(part, ignoreCase: false, out var kind) || !Enum.IsDefined(kind))
                    throw new DocumentValidationException($"'{part}' is not a kind of bundle item.");
                wanted.Add(kind);
            }

            var bundle = await service.ExportAsync(name ?? string.Empty, wanted, await users.ResolveAsync(http, ct), ct);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Cache-Control"] = "no-store";
            http.Response.Headers["Content-Disposition"] = "attachment; filename=\"qahwa-bundle.json\"";
            return Results.Text(BundleService.ToJson(bundle), "application/json");
        });

        // The body is the bundle file itself. It must be sent as application/json, which a web page on another site cannot do without a CORS preflight.
        group.MapPost("/import", async (bool? dryRun, HttpContext http, IUserContextResolver users, BundleService service, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!http.Request.HasJsonContentType())
                return Results.Json(new { code = "validation_error", message = "Send the bundle as application/json." }, statusCode: 415);
            if (http.Request.ContentLength > MaxBytes)
                return Results.Json(new { code = "validation_error", message = "The bundle is too large (5 MB at most)." }, statusCode: 413);

            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return Results.Json(new { code = "validation_error", message = "The bundle is too large (5 MB at most)." }, statusCode: 413);
                buffer.Write(chunk, 0, read);
            }

            var bundle = BundleService.FromJson(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
            return Results.Ok(new { data = await service.ImportAsync(bundle, dryRun ?? true, user, ct) });
        });

        return app;
    }
}
