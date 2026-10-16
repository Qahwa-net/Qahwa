using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>The translation catalog of a language, for the Desk to show its words in that language.</summary>
public static class TranslationEndpoints
{
    public static IEndpointRouteBuilder MapQahwaTranslations(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/translations");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, TranslationService service, CancellationToken ct) =>
            Results.Ok(new { data = await service.LanguagesAsync(await users.ResolveAsync(http, ct), ct) }));

        group.MapGet("/{language}", async (string language, HttpContext http, IUserContextResolver users, TranslationService service, CancellationToken ct) =>
            Results.Ok(new { data = new { language, rtl = TranslationService.IsRightToLeft(language), messages = await service.CatalogAsync(language, await users.ResolveAsync(http, ct), ct) } }));

        return app;
    }
}
