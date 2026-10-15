using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Qahwa.Core;

namespace Qahwa.Api;

public sealed record RenameRequest(string? NewName, bool Merge = false);

public sealed record SeriesRequest(string? Prefix, long Current);

/// <summary>Writes rows as CSV for download, and keeps spreadsheets from running a cell as a formula.</summary>
public static class CsvExport
{
    public static string Write(IReadOnlyList<string> columns, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var csv = new StringBuilder();
        csv.AppendJoin(',', columns.Select(c => Cell(c, text: true))).Append("\r\n");
        foreach (var row in rows)
            csv.AppendJoin(',', columns.Select(c => Cell(row.GetValueOrDefault(c)))).Append("\r\n");
        return csv.ToString();
    }

    private static string Cell(object? value, bool text = false)
    {
        var (content, isText) = value switch
        {
            null => (string.Empty, false),
            bool b => (b ? "1" : "0", false),
            DateTime d => (d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), false),
            DateOnly d => (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false),
            TimeOnly t => (t.ToString("HH:mm:ss", CultureInfo.InvariantCulture), false),
            IFormattable number when value is not string => (number.ToString(null, CultureInfo.InvariantCulture), false),
            _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, true),
        };

        // Text starting with = + - @ (or a control character) can be run as a formula by spreadsheet programs.
        if ((isText || text) && content.Length > 0 && (content[0] is '=' or '+' or '-' or '@' or '\t' or '\r'))
            content = "'" + content;
        return content.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + content.Replace("\"", "\"\"") + "\"" : content;
    }
}

public static class SpecialEndpoints
{
    public static IEndpointRouteBuilder MapQahwaSpecial(this IEndpointRouteBuilder app)
    {
        var resource = app.MapGroup("/api/resource/{doctype}");

        resource.MapPost("/{name}/rename", async (
            string doctype, string name, RenameRequest body, HttpContext http,
            DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var newName = await docs.RenameAsync(doctype, name, body.NewName ?? string.Empty, body.Merge, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { data = new { name = newName } });
        });

        resource.MapGet("/export", async (
            string doctype, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var result = await docs.ExportAsync(doctype, QahwaEndpoints.ParseListRequest(http.Request.Query), await users.ResolveAsync(http, ct), ct);
            var fileName = new string(doctype.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
            http.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{(fileName.Length == 0 ? "export" : fileName)}.csv\"";
            http.Response.Headers["X-Export-Truncated"] = result.Truncated ? "true" : "false";
            return Results.Text(CsvExport.Write(result.Columns, result.Rows), "text/csv", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        });

        resource.MapGet("/report", async (
            string doctype, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var query = http.Request.Query;
            var result = await docs.ReportAsync(
                doctype, query["group_by"].ToString(), query["aggregate"].ToString(), query["field"].ToString() is { Length: > 0 } f ? f : null,
                QahwaEndpoints.ParseListRequest(query), await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { data = result.Rows, truncated = result.Truncated });
        });

        // Link fields: type-ahead search, and the friendly names of values already chosen.
        app.MapGet("/api/link/{doctype}/search", async (
            string doctype, string? q, int? limit, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
            Results.Ok(new { data = await docs.SearchLinkAsync(doctype, q, limit ?? 10, await users.ResolveAsync(http, ct), ct) }));

        app.MapGet("/api/link/{doctype}/titles", async (
            string doctype, string? names, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            List<string> wanted;
            try
            {
                wanted = string.IsNullOrWhiteSpace(names) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(names) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                throw new DocumentValidationException("'names' must be a JSON array of names.");
            }

            return Results.Ok(new { data = await docs.LinkTitlesAsync(doctype, wanted, await users.ResolveAsync(http, ct), ct) });
        });

        // The list sidebar: how many documents have each value of a field, under the list's current filters.
        resource.MapGet("/group-by", async (
            string doctype, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
        {
            var query = http.Request.Query;
            var limit = int.TryParse(query["limit"], out var n) ? n : 20;
            var groups = await docs.GroupByAsync(doctype, query["field"].ToString(), QahwaEndpoints.ParseListRequest(query), limit, await users.ResolveAsync(http, ct), ct);
            return Results.Ok(new { data = groups });
        });

        // Comment counts for the rows of a list (names as a JSON array, at most 100).
        resource.MapGet("/comment-counts", async (
            string doctype, string? names, HttpContext http, ActivityService activity, IUserContextResolver users, CancellationToken ct) =>
        {
            List<string> wanted;
            try
            {
                wanted = string.IsNullOrWhiteSpace(names) ? [] : System.Text.Json.JsonSerializer.Deserialize<List<string>>(names) ?? [];
            }
            catch (System.Text.Json.JsonException)
            {
                throw new DocumentValidationException("'names' must be a JSON array of names.");
            }

            return Results.Ok(new { data = await activity.CommentCountsAsync(doctype, wanted, await users.ResolveAsync(http, ct), ct) });
        });

        app.MapGet("/api/tree/{doctype}", async (
            string doctype, string? parent, HttpContext http, DocumentService docs, IUserContextResolver users, CancellationToken ct) =>
            Results.Ok(new { data = await docs.TreeChildrenAsync(doctype, parent, await users.ResolveAsync(http, ct), ct) }));

        var naming = app.MapGroup("/api/naming");
        naming.MapGet("/series", async (
            HttpContext http, NamingSeriesManager manager, IUserContextResolver users, CancellationToken ct) =>
            Results.Ok(new { data = await manager.ListAsync(await users.ResolveAsync(http, ct), ct) }));
        naming.MapPut("/series", async (
            SeriesRequest body, HttpContext http, NamingSeriesManager manager, IUserContextResolver users, CancellationToken ct) =>
        {
            var user = await users.ResolveAsync(http, ct);
            await manager.SetAsync(body.Prefix ?? string.Empty, body.Current, user, ct);
            return Results.Ok(new { data = await manager.ListAsync(user, ct) });
        });

        return app;
    }
}
