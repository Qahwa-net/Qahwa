using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record ScriptTestRequest(string? Doctype, string? Event, string? Script, Dictionary<string, JsonElement>? Doc, [property: JsonPropertyName("is_new")] bool? IsNew);

/// <summary>Try a Server Script on a sample document without saving anything (System Manager only).</summary>
public static class ServerScriptEndpoints
{
    public static IEndpointRouteBuilder MapQahwaServerScripts(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/server-scripts/test", async (ScriptTestRequest body, HttpContext http, IUserContextResolver users, IMetaRegistry registry, ServerScriptEngine scripts, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            if (!user.Roles.Contains("System Manager") && !user.Roles.Contains(UserContext.AdministratorRole))
                throw new PermissionDeniedException("Only a System Manager can try scripts.");

            var meta = registry.Get(body.Doctype ?? string.Empty);
            var kind = body.Event is ServerScriptEngine.ValidateEvent or ServerScriptEngine.AfterSaveEvent
                ? body.Event
                : throw new DocumentValidationException([new FieldError("event", "The event must be Validate or After Save.")]);
            var doc = (body.Doc ?? []).ToDictionary(kv => kv.Key, kv => Plain(kv.Value), StringComparer.Ordinal);
            var outcome = await scripts.TestAsync(meta, kind, body.Script ?? string.Empty, doc, body.IsNew ?? true, user, ct);
            return Results.Ok(new { data = new { changes = outcome.Changes, log = outcome.Log, reads = outcome.Reads } });
        });

        return app;
    }

    private static object? Plain(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var l) ? l : value.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };
}
