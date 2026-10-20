using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>Server functions: the built-in <c>qahwa.*</c> ones and those registered by installed apps.</summary>
public static class MethodEndpoints
{
    public static IEndpointRouteBuilder MapQahwaMethods(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/method");

        group.MapGet("", async (HttpContext http, IUserContextResolver users, MethodService methods, AppInstaller installer, CancellationToken ct) =>
        {
            if (!(await users.ResolveAsync(http, ct)).IsAuthenticated)
                throw new UnauthenticatedException();
            return Results.Ok(new { data = new { methods = methods.Names.Order(StringComparer.Ordinal), apps = await installer.ListAsync(ct) } });
        });

        // Switch an app on or off for this site (System Managers). Data and DocTypes stay; hooks, methods, jobs and reports stop.
        app.MapPost("/api/apps/{name}/{action:regex(^(install|uninstall)$)}", async (string name, string action, HttpContext http, IUserContextResolver users, AppInstaller installer, AccessLogService audit, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.IsAuthenticated)
                throw new UnauthenticatedException();
            if (!UserSecurityEndpoints.CanManage(user))
                throw new PermissionDeniedException("Only a System Manager can install or uninstall apps.");
            if (action == "install")
                await installer.InstallAsync(name, ct);
            else
                await installer.UninstallAsync(name, ct);
            await audit.RecordAsync(action == "install" ? "Install App" : "Uninstall App", "App", name, 1, user, ct);
            return Results.Ok(new { data = await installer.ListAsync(ct) });
        });

        // What installed apps hand the Desk at start-up (one object per app that has a boot hook).
        app.MapGet("/api/boot", async (HttpContext http, IUserContextResolver users, BootService boot, CancellationToken ct) =>
        {
            http.Response.Headers["Cache-Control"] = "no-store";
            return Results.Ok(new { data = new { apps = await boot.CollectAsync(await users.ResolveAsync(http, ct), ct) } });
        });

        // Reports written in code by installed apps (ADR-088).
        app.MapGet("/api/script-reports", async (HttpContext http, IUserContextResolver users, ScriptReportService reports, CancellationToken ct) =>
            Results.Ok(new { data = reports.List(await users.ResolveAsync(http, ct)) }));

        app.MapPost("/api/script-reports/{id}/run", async (string id, HttpContext http, IUserContextResolver users, ScriptReportService reports, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            Dictionary<string, string>? filters = null;
            using (var doc = await ReadBodyAsync(http, ct))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("filters", out var f) && f.ValueKind == JsonValueKind.Object)
                    filters = f.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Take(20).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
            }

            return Results.Ok(new { data = await reports.RunAsync(id, filters, user, ct) });
        });

        group.MapPost("/{name}", async (string name, HttpContext http, IUserContextResolver users, MethodService methods, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            JsonElement args;
            using (var doc = await ReadBodyAsync(http, ct))
                args = doc.RootElement.Clone();
            return Results.Ok(new { data = await methods.InvokeAsync(name, args, user, isGet: false, ct) });
        });

        // Reading functions may also be called with GET (arguments in the address; each value is text, or JSON for lists).
        group.MapGet("/{name}", async (string name, HttpContext http, IUserContextResolver users, MethodService methods, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var (key, value) in http.Request.Query)
            {
                var text = value.ToString();
                values[key] = text.StartsWith('[') || text.StartsWith('{') ? JsonDocument.Parse(text.Length > MethodService.MaxBodyBytes ? "[]" : text).RootElement.Clone() : JsonSerializer.SerializeToElement(text);
            }

            return Results.Ok(new { data = await methods.InvokeAsync(name, JsonSerializer.SerializeToElement(values), user, isGet: true, ct) });
        });

        return app;
    }

    /// <summary>The JSON body (an object, or nothing), refused when it is larger than the limit.</summary>
    private static async Task<JsonDocument> ReadBodyAsync(HttpContext http, CancellationToken ct)
    {
        if (http.Request.ContentLength is > MethodService.MaxBodyBytes)
            throw new DocumentValidationException([new FieldError(string.Empty, "The request is too large.")]);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await http.Request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MethodService.MaxBodyBytes)
                throw new DocumentValidationException([new FieldError(string.Empty, "The request is too large.")]);
            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
            return JsonDocument.Parse("{}");
        try
        {
            var doc = JsonDocument.Parse(buffer.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new DocumentValidationException([new FieldError(string.Empty, "Send the arguments as a JSON object.")]);
            return doc;
        }
        catch (JsonException)
        {
            throw new DocumentValidationException([new FieldError(string.Empty, "The request is not valid JSON.")]);
        }
    }
}
