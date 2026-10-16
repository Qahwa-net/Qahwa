using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>A printout as a PDF file (when this server has a browser set up to make them).</summary>
public static class PdfEndpoints
{
    public static IEndpointRouteBuilder MapQahwaPdf(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pdf/status", async (HttpContext http, IUserContextResolver users, PdfService pdf, CancellationToken ct) =>
        {
            if (!(await users.ResolveAsync(http, ct)).IsAuthenticated)
                throw new UnauthenticatedException();
            return Results.Ok(new { data = new { available = pdf.Available } });
        });

        app.MapGet("/api/pdf/{doctype}/{name}", async (
            string doctype, string name, string? format, string? letterhead, HttpContext http, IUserContextResolver users, PdfService pdf, CancellationToken ct) =>
        {
            var file = await pdf.RenderAsync(doctype, name, format, letterhead, await users.ResolveAsync(http, ct), ct);
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.File(file.Content, "application/pdf", file.FileName);
        });

        return app;
    }
}
