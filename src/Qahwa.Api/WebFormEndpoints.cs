using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>
/// Public forms, open to anyone. These endpoints never look at the cookie or an API key (so another site cannot make a signed-in
/// person's browser do anything here), accept only a small JSON object, and are rate limited.
/// </summary>
public static class WebFormEndpoints
{
    public const int MaxBytes = 32 * 1024;
    public const string RateLimit = "qahwa-public-form";

    public static IEndpointRouteBuilder MapQahwaWebForms(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/public/forms").RequireRateLimiting(RateLimit);

        group.MapGet("/{route}", async (string route, HttpContext http, WebFormService service, CancellationToken ct) =>
        {
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { data = await service.GetAsync(route, ct) });
        });

        group.MapPost("/{route}", async (string route, HttpContext http, WebFormService service, CancellationToken ct) =>
        {
            if (!http.Request.HasJsonContentType())
                return Results.Json(new { code = "bad_request", message = "Send the answers as application/json." }, statusCode: 415);
            if (http.Request.ContentLength > MaxBytes)
                return Results.Json(new { code = "bad_request", message = "That is too much data." }, statusCode: 413);

            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                    return Results.Json(new { code = "bad_request", message = "That is too much data." }, statusCode: 413);
                buffer.Write(chunk, 0, read);
            }

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(buffer.ToArray()).RootElement;
            }
            catch (JsonException)
            {
                return Results.Json(new { code = "bad_request", message = "The answers are not valid JSON." }, statusCode: 400);
            }

            if (root.ValueKind != JsonValueKind.Object)
                return Results.Json(new { code = "bad_request", message = "Send the answers as a JSON object." }, statusCode: 400);

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.TryGetInt64(out var whole) ? whole : property.Value.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => throw new DocumentValidationException([new FieldError(property.Name, "That is not a valid answer.")]),
                };
            }

            var message = await service.SubmitAsync(route, values, http.Connection.RemoteIpAddress?.ToString() ?? "unknown", ct);
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { message });
        });

        return app;
    }
}
