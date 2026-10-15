using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record PrintPreviewRequest(string? DocType = null, string? Name = null, string? Html = null, string? LetterHead = null);

/// <summary>Printing documents, and managing print formats and letter heads (System Managers).</summary>
public static class PrintEndpoints
{
    public static IEndpointRouteBuilder MapQahwaPrint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/print/choices", async (string doctype, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
            Results.Ok(new { data = await print.ChoicesAsync(doctype, await users.ResolveAsync(http, ct), ct) }));

        // The page itself: HTML for paper. A strict policy means that even if a format slipped something past the checks, it could not run.
        app.MapGet("/api/print/{doctype}/{name}", async (
            string doctype, string name, string? format, string? letterhead, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
        {
            var page = await print.RenderAsync(doctype, name, format, letterhead, await users.ResolveAsync(http, ct), ct);
            Secure(http, page.Nonce);
            return Results.Content(page.Html, "text/html; charset=utf-8");
        });

        var manage = app.MapGroup("/api/print-formats");

        manage.MapGet("", async (HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
            Results.Ok(new { data = await print.ListFormatsAsync(await users.ResolveAsync(http, ct), ct) }));

        manage.MapGet("/{name}", async (string name, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
            Results.Ok(new { data = await print.GetFormatAsync(name, await users.ResolveAsync(http, ct), ct) }));

        manage.MapPut("", async (PrintFormatRequest body, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
            Results.Ok(new { data = await print.SaveFormatAsync(body, await users.ResolveAsync(http, ct), ct) }));

        manage.MapDelete("/{name}", async (string name, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
        {
            await print.DeleteFormatAsync(name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        manage.MapPost("/preview", async (PrintPreviewRequest body, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
        {
            var page = await print.PreviewAsync(body.DocType ?? string.Empty, body.Name ?? string.Empty, body.Html ?? string.Empty, body.LetterHead, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { data = new { html = page.Html } });
        });

        var heads = app.MapGroup("/api/letter-heads");

        heads.MapGet("", async (HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
            Results.Ok(new { data = await print.ListLetterHeadsAsync(await users.ResolveAsync(http, ct), ct) }));

        heads.MapPut("", async (LetterHeadRequest body, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
            Results.Ok(new { data = await print.SaveLetterHeadAsync(body, await users.ResolveAsync(http, ct), ct) }));

        heads.MapDelete("/{name}", async (string name, HttpContext http, IUserContextResolver users, PrintService print, CancellationToken ct) =>
        {
            await print.DeleteLetterHeadAsync(name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }

    private static void Secure(HttpContext http, string nonce)
    {
        http.Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; style-src 'unsafe-inline'; img-src data: https:; font-src data:; script-src 'nonce-{nonce}'; base-uri 'none'; form-action 'none'; frame-ancestors 'self'";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers["Cache-Control"] = "no-store";
    }
}
