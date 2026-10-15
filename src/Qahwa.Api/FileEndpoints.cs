using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Attachments: list, upload (raw bytes with an X-File-Name header), download, delete.</summary>
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapQahwaFiles(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/files");

        group.MapGet("/for/{doctype}/{name}", async (string doctype, string name, HttpContext http, IUserContextResolver users, FileService files, CancellationToken ct) =>
            Results.Ok(new { data = await files.ListAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        // The file's own name travels in a header (percent-encoded UTF-8): a custom header cannot be sent cross-site without a CORS preflight,
        // so a page on another site cannot upload on someone's behalf.
        group.MapPost("/for/{doctype}/{name}", async (string doctype, string name, HttpContext http, IUserContextResolver users, FileService files, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            var encoded = http.Request.Headers["X-File-Name"].ToString();
            if (encoded.Length == 0 || encoded.Length > 600)
                throw new DocumentValidationException([new FieldError("file", "Send the file's name in the X-File-Name header.")]);
            string fileName;
            try
            {
                fileName = WebUtility.UrlDecode(encoded);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new DocumentValidationException([new FieldError("file", "The file name is not valid.")]);
            }

            if (http.Request.ContentLength is { } length && length > files.MaxBytes)
                throw new DocumentValidationException([new FieldError("file", $"The file is larger than {files.MaxBytes / (1024 * 1024)} MB.")]);
            var created = await files.UploadAsync(doctype, name, fileName, http.Request.Body, user, ct);
            return Results.Created($"/api/files/{created.Id}", new { data = created });
        });

        group.MapGet("/{id}", async (string id, HttpContext http, IUserContextResolver users, FileService files, CancellationToken ct) =>
        {
            var file = await files.OpenAsync(id, await users.ResolveAsync(http, ct), ct);
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Cache-Control"] = "private, no-store";
            // A sandboxed policy as well, so even a file that gets opened in the browser can run nothing.
            http.Response.Headers["Content-Security-Policy"] = "default-src 'none'; sandbox";
            var safeName = Uri.EscapeDataString(file.FileName);
            http.Response.Headers["Content-Disposition"] = (file.Inline ? "inline" : "attachment") + $"; filename=\"file\"; filename*=UTF-8''{safeName}";
            return Results.Stream(file.Content, file.ContentType);
        });

        group.MapDelete("/{id}", async (string id, HttpContext http, IUserContextResolver users, FileService files, CancellationToken ct) =>
        {
            await files.DeleteAsync(id, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
