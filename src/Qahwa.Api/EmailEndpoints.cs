using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Email about a document: templates to pick from, a preview, and sending (queued).</summary>
public static class EmailEndpoints
{
    public static IEndpointRouteBuilder MapQahwaEmail(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/email/templates", async (string doctype, HttpContext http, IUserContextResolver users, EmailService email, CancellationToken ct) =>
            Results.Ok(new { data = await email.TemplatesAsync(doctype, await users.ResolveAsync(http, ct), ct) }));

        var doc = app.MapGroup("/api/resource/{doctype}/{name}/email");

        doc.MapPost("/preview", async (string doctype, string name, EmailRequest body, HttpContext http, IUserContextResolver users, EmailService email, CancellationToken ct) =>
            Results.Ok(new { data = await email.PreviewAsync(doctype, name, body.Template ?? string.Empty, await users.ResolveAsync(http, ct), ct) }));

        doc.MapPost("", async (string doctype, string name, EmailRequest body, HttpContext http, IUserContextResolver users, EmailService email, CancellationToken ct) =>
        {
            var queued = await email.SendFromDocumentAsync(doctype, name, body, await users.ResolveAsync(http, ct), ct);
            return Results.Accepted(value: new { data = new { queued } });
        });

        return app;
    }
}
