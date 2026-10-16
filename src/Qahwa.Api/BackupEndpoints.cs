using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Download and restore a backup (Administrator only). One at a time: a backup reads the whole database.</summary>
public static class BackupEndpoints
{
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public static IEndpointRouteBuilder MapQahwaBackup(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/backup");

        group.MapGet("/download", async (HttpContext http, IUserContextResolver users, BackupService backup, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            backup.Require(user);
            if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
                throw new PermissionDeniedException("A backup cannot be made with an API key. Sign in to do that.");
            if (!await OneAtATime.WaitAsync(0, ct))
                return Results.Json(new { code = "busy", message = "A backup or restore is already running. Try again in a few minutes." }, statusCode: 429);

            // Built in a temporary file first (a zip cannot be written straight to the response, and a failure can still be a normal error),
            // then sent; the file is deleted when the last byte has gone.
            FileStream temp;
            try
            {
                temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                try
                {
                    await backup.WriteAsync(temp, user, ct);
                }
                catch
                {
                    await temp.DisposeAsync();
                    throw;
                }
            }
            finally
            {
                OneAtATime.Release();
            }

            temp.Position = 0;
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.File(temp, "application/zip", $"qahwa-backup-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip");
        });

        // The body is the zip itself, sent as application/zip (which another site cannot send without a CORS preflight).
        group.MapPost("/restore", async (bool? dryRun, HttpContext http, IUserContextResolver users, BackupService backup, BackupOptions options, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            backup.Require(user);
            if (http.Items.ContainsKey(CookieUserContextResolver.ViaApiKey))
                throw new PermissionDeniedException("A backup cannot be restored with an API key. Sign in to do that.");
            if (!string.Equals(http.Request.ContentType?.Split(';')[0].Trim(), "application/zip", StringComparison.OrdinalIgnoreCase))
                return Results.Json(new { code = "bad_request", message = "Send the backup as application/zip." }, statusCode: 415);

            var limit = Math.Max(options.MaxRestoreMegabytes, 1) * 1024L * 1024L;
            if (http.Request.ContentLength > limit)
                return Results.Json(new { code = "bad_request", message = "That backup is larger than this system will read back." }, statusCode: 413);
            if (!await OneAtATime.WaitAsync(0, ct))
                return Results.Json(new { code = "busy", message = "A backup or restore is already running. Try again in a few minutes." }, statusCode: 429);

            try
            {
                // A zip needs to be read from the end, so it is kept in a temporary file (deleted when done) while it is read.
                await using var temp = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose);
                var chunk = new byte[81920];
                long total = 0;
                int read;
                while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    total += read;
                    if (total > limit)
                        return Results.Json(new { code = "bad_request", message = "That backup is larger than this system will read back." }, statusCode: 413);
                    await temp.WriteAsync(chunk.AsMemory(0, read), ct);
                }

                temp.Position = 0;
                return Results.Ok(new { data = await backup.RestoreAsync(temp, dryRun ?? true, user, ct, limit) });
            }
            finally
            {
                OneAtATime.Release();
            }
        });

        return app;
    }
}
