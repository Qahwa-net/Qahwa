using System.Globalization;
using System.Text.Json;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record ScriptReportColumn(string Key, string Label, string Type = "Data");

/// <summary>What an app's report returns: its columns and rows (each row a value for every column key).</summary>
public sealed record ScriptReportData(IReadOnlyList<ScriptReportColumn> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows);

/// <summary>What the person sees: the table, whether rows were cut off, and the filters in effect.</summary>
public sealed record ScriptReportResult(string Id, string Title, IReadOnlyList<ScriptReportColumn> Columns, IReadOnlyList<Dictionary<string, object?>> Rows, bool Truncated);

public sealed record ScriptReportInfo(string Id, string Title, string App, IReadOnlyList<string> Filters);

/// <summary>Who may run a report, and which filters it takes (each a short text the person types).</summary>
public sealed record ScriptReportOptions
{
    /// <summary>A person needs one of these roles (empty: any signed-in person). Administrators always may.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>The names of the filters the report understands. Anything else sent is ignored.</summary>
    public IReadOnlyList<string> Filters { get; init; } = [];
}

public sealed class ScriptReportContext(IUserContext user, IReadOnlyDictionary<string, string> filters, DocumentService documents, IServiceProvider services, CancellationToken ct)
{
    public IUserContext User { get; } = user;

    /// <summary>The filters the person gave, only those the report declared, each at most 200 characters.</summary>
    public IReadOnlyDictionary<string, string> Filters { get; } = filters;

    /// <summary>Reads documents as this person, with their permissions: a report built on it shows no more than the person may see.</summary>
    public DocumentService Documents { get; } = documents;

    public IServiceProvider Services { get; } = services;
    public CancellationToken Cancellation { get; } = ct;
}

public delegate Task<ScriptReportData> ScriptReportHandler(ScriptReportContext context);

internal sealed record RegisteredScriptReport(string App, string Name, string Title, ScriptReportHandler Handler, ScriptReportOptions Options)
{
    public string Id => $"{App}.{Name}";
}

/// <summary>
/// Script Reports: reports an app writes in code (anything that can be computed: joins, calculations, other systems), listed and run
/// next to the report builder. There is deliberately no way to type SQL in the Desk: a report is code an installed app ships, so it
/// is as reviewed as the rest of the app, and it reads documents through the person's own permissions. The service limits what a
/// report may return (rows, columns, text) and never shows an app's error to the person.
/// </summary>
public sealed class ScriptReportService(AppCatalog catalog, IServiceProvider services, Lazy<DocumentService> documents, ErrorLogService errors, AccessLogService? accessLog = null)
{
    public const int MaxRows = 5000;
    public const int MaxColumns = 50;
    public const int MaxFilterLength = 200;
    public const int MaxCell = 1000;

    private static bool May(RegisteredScriptReport report, IUserContext user) =>
        user.Roles.Contains(UserContext.AdministratorRole) || report.Options.Roles.Count == 0 || report.Options.Roles.Any(r => user.Roles.Contains(r));

    public IReadOnlyList<ScriptReportInfo> List(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        return catalog.ScriptReports.Where(r => May(r, user)).OrderBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .Select(r => new ScriptReportInfo(r.Id, r.Title, r.App, r.Options.Filters)).ToList();
    }

    public async Task<ScriptReportResult> RunAsync(string id, IReadOnlyDictionary<string, string>? filters, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        // A report the person may not run looks the same as one that does not exist.
        var report = catalog.ScriptReports.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.Ordinal) && May(r, user))
            ?? throw new NotFoundException("There is no such report.");

        var given = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in report.Options.Filters)
        {
            if (filters is not null && filters.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                given[name] = value.Length > MaxFilterLength ? value[..MaxFilterLength] : value;
        }

        ScriptReportData data;
        try
        {
            data = await report.Handler(new ScriptReportContext(user, given, documents.Value, services, ct));
        }
        catch (Exception e) when (e is QahwaException)
        {
            throw; // a refusal made through the document service (permission, validation) is the person's to see
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await errors.RecordAsync("Script report", null, report.Id, user.User, e);
            throw new DocumentValidationException("The report could not be run. The problem has been recorded.");
        }

        var columns = data.Columns.Take(MaxColumns).Select(c => new ScriptReportColumn(Clip(c.Key, 80), Clip(c.Label, 140), Clip(c.Type, 40))).ToList();
        if (columns.Count == 0 || columns.Select(c => c.Key).Distinct(StringComparer.Ordinal).Count() != columns.Count)
            throw new DocumentValidationException("The report has no columns, or two columns with the same key.");
        var rows = data.Rows.Take(MaxRows).Select(r => columns.ToDictionary(c => c.Key, c => Tidy(r.GetValueOrDefault(c.Key)), StringComparer.Ordinal)).ToList();
        if (accessLog is not null)
            await accessLog.RecordAsync("Export", $"Report {report.Id}", null, rows.Count, user, ct);
        return new ScriptReportResult(report.Id, report.Title, columns, rows, data.Rows.Count > MaxRows);
    }

    private static string Clip(string text, int max) => text.Length > max ? text[..max] : text;

    /// <summary>A cell as plain data: text, numbers, yes/no, dates as ISO text; anything else as its text. Long text is cut.</summary>
    private static object? Tidy(object? value) => value switch
    {
        null => null,
        string s => Clip(s, MaxCell),
        bool or int or long or double or decimal => value,
        DateTime d => d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        JsonElement j => Clip(j.ToString(), MaxCell),
        IFormattable f => Clip(f.ToString(null, CultureInfo.InvariantCulture), MaxCell),
        _ => Clip(value.ToString() ?? string.Empty, MaxCell),
    };
}
