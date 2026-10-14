using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;
using Qahwa.Core.Designer;

namespace Qahwa.Api;

/// <param name="Definition">The DocType as the designer sees it.</param>
/// <param name="Modified">The version the client loaded; saving fails if someone else changed it since.</param>
/// <param name="Existing">For previews of an edit: the name of the DocType being edited.</param>
public sealed record DesignerRequest(DocTypeMeta Definition, DateTime? Modified = null, string? Existing = null);

public static class DesignerEndpoints
{
    public static IEndpointRouteBuilder MapQahwaDesigner(this IEndpointRouteBuilder app)
    {
        var designer = app.MapGroup("/api/designer");

        designer.MapGet("/field-types", async (
            HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
        {
            await service.ListAsync(await users.ResolveAsync(http, ct), ct); // same access check as the rest
            return Results.Ok(new { data = FieldTypeCatalog.All });
        });

        designer.MapGet("/doctypes", async (
            HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
            Results.Ok(new { data = await service.ListAsync(await users.ResolveAsync(http, ct), ct) }));

        designer.MapGet("/doctypes/{name}", async (
            string name, HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
            Results.Ok(new { data = await service.GetAsync(name, await users.ResolveAsync(http, ct), ct) }));

        designer.MapPost("/preview", async (
            DesignerRequest body, HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
            Results.Ok(new { data = await service.PreviewAsync(body.Definition, body.Existing, await users.ResolveAsync(http, ct), ct) }));

        designer.MapPost("/doctypes", async (
            DesignerRequest body, HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
        {
            var detail = await service.CreateAsync(body.Definition, await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/designer/doctypes/{Uri.EscapeDataString(detail.Definition.Name)}", new { data = detail });
        });

        designer.MapPut("/doctypes/{name}", async (
            string name, DesignerRequest body, HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
            Results.Ok(new { data = await service.UpdateAsync(name, body.Definition, body.Modified, await users.ResolveAsync(http, ct), ct) }));

        designer.MapDelete("/doctypes/{name}", async (
            string name, HttpContext http, IUserContextResolver users, DocTypeDesigner service, CancellationToken ct) =>
        {
            await service.DeleteAsync(name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        return app;
    }
}
