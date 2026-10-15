using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>The optional body of submit and cancel: the "modified" value the client loaded, for conflict detection.</summary>
public sealed record TransitionRequest(DateTime? Modified = null);

public static class QahwaEndpoints
{
    public static IEndpointRouteBuilder MapQahwaApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/meta", async (HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
            Results.Ok(new
            {
                data = (await docs.ListMetasAsync(await users.ResolveAsync(http, ct), ct)).Select(m => new { m.Name, m.Module, m.TitleField }),
            }));

        api.MapGet("/meta/{doctype}", async (
            string doctype, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var view = await docs.GetMetaViewAsync(doctype, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new
            {
                data = view.Meta,                // only the fields this person may read, without permission rules
                permissions = view.Permissions,
                children = view.Children,        // the Desk needs them to draw the table grids
                writableLevels = view.WritableLevels,
            });
        });

        var resource = api.MapGroup("/resource/{doctype}");

        resource.MapGet(string.Empty, async (
            string doctype, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var result = await docs.ListAsync(
                doctype, ParseListRequest(http.Request.Query), await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { data = result.Data, total = result.Total });
        });

        resource.MapPost(string.Empty, async (
            string doctype, Dictionary<string, JsonElement> body, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var doc = await docs.InsertAsync(doctype, ToValues(body), await users.ResolveAsync(http, ct), ct);
            return Results.Created($"/api/resource/{doctype}/{Uri.EscapeDataString((string)doc["name"]!)}", new { data = doc });
        });

        resource.MapGet("/{name}", async (
            string doctype, string name, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
            Results.Ok(new { data = await docs.GetAsync(doctype, name, await users.ResolveAsync(http, ct), ct) }));

        resource.MapPut("/{name}", async (
            string doctype, string name, Dictionary<string, JsonElement> body, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
            Results.Ok(new
            {
                data = await docs.UpdateAsync(doctype, name, ToValues(body), await users.ResolveAsync(http, ct), ct),
            }));

        // Submittable DocTypes: docstatus 0 (draft) to 1 (submitted) to 2 (cancelled). The body may carry the "modified" the client saw.
        resource.MapPost("/{name}/submit", async (
            string doctype, string name, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
                Results.Ok(new { data = await docs.SubmitAsync(doctype, name, await users.ResolveAsync(http, ct), (await ReadTransitionAsync(http, ct))?.Modified, ct) }));

        resource.MapPost("/{name}/cancel", async (
            string doctype, string name, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
                Results.Ok(new { data = await docs.CancelAsync(doctype, name, await users.ResolveAsync(http, ct), (await ReadTransitionAsync(http, ct))?.Modified, ct) }));

        resource.MapDelete("/{name}", async (
            string doctype, string name, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            await docs.DeleteAsync(doctype, name, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { message = "ok" });
        });

        app.MapQahwaAuth();
        app.MapQahwaDesigner();
        app.MapQahwaPermissions();
        app.MapQahwaSharing();
        app.MapQahwaSpecial();
        app.MapQahwaCustomize();
        app.MapQahwaActivity();
        app.MapQahwaCollaboration();
        app.MapQahwaLists();
        app.MapQahwaWorkflows();
        return app;
    }

    /// <summary>The body of submit and cancel is optional, so a plain POST with no body works.</summary>
    private static async Task<TransitionRequest?> ReadTransitionAsync(HttpContext http, CancellationToken ct) =>
        http.Request.ContentLength is > 0 ? await http.Request.ReadFromJsonAsync<TransitionRequest>(ct) : null;

    private static Dictionary<string, object?> ToValues(Dictionary<string, JsonElement> body) =>
        body.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);

    /// <summary>
    /// Reads Frappe-style list parameters: <c>filters</c> (JSON array of [field, op, value] or an object of
    /// equality pairs), <c>fields</c> (JSON array), <c>order_by</c> ("field desc"), <c>limit_start</c>, <c>limit_page_length</c>.
    /// </summary>
    internal static ListRequest ParseListRequest(IQueryCollection query)
    {
        try
        {
            var orderBy = query["order_by"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return new ListRequest
            {
                Filters = ParseFilters(query["filters"].ToString()),
                Fields = ParseFields(query["fields"].ToString()),
                OrderBy = orderBy.Length > 0 ? orderBy[0] : null,
                Descending = orderBy.Length < 2 || !orderBy[1].Equals("asc", StringComparison.OrdinalIgnoreCase),
                Limit = ParseInt(query["limit_page_length"], 20),
                Offset = ParseInt(query["limit_start"], 0),
            };
        }
        catch (JsonException)
        {
            throw new DocumentValidationException("The 'filters' or 'fields' parameter is not valid JSON.");
        }
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, out var n) ? n : fallback;

    private static List<string>? ParseFields(string json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<List<string>>(json);

    private static List<RawFilter> ParseFilters(string json)
    {
        var filters = new List<RawFilter>();
        if (string.IsNullOrWhiteSpace(json))
            return filters;

        using var doc = JsonDocument.Parse(json);
        switch (doc.RootElement.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in doc.RootElement.EnumerateObject())
                    filters.Add(new RawFilter(property.Name, "=", property.Value.Clone()));
                break;
            case JsonValueKind.Array:
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() != 3
                        || item[0].ValueKind != JsonValueKind.String || item[1].ValueKind != JsonValueKind.String)
                    {
                        throw new DocumentValidationException("Each filter must look like [\"field\", \"operator\", value].");
                    }

                    filters.Add(new RawFilter(item[0].GetString()!, item[1].GetString()!, item[2].Clone()));
                }

                break;
            default:
                throw new DocumentValidationException("'filters' must be a JSON array or object.");
        }

        return filters;
    }
}
