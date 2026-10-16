namespace Qahwa.Core;

/// <summary>
/// Keeps unexpected errors (the ones people only see as "an unexpected error occurred") where a System Manager can read them: what
/// failed, where, for whom, and the stack. It never records the request body, headers or query string (they can hold secrets),
/// is limited so an error storm cannot fill the database, and can never itself throw.
/// </summary>
public sealed class ErrorLogService(DocumentService documents, HitCounter hits)
{
    public const string DocTypeName = "Error Log";
    public const int MaxMessage = 1000;
    public const int MaxTrace = 8000;
    public const int MaxPerMinute = 30;
    public const int MaxPerHour = 500;

    private static readonly UserContext System = new("system@qahwa", [UserContext.AdministratorRole]);

    public async Task RecordAsync(string source, string? method, string? path, string? user, Exception error, CancellationToken ct = default)
    {
        try
        {
            if (!hits.TryHit("error-log:minute", MaxPerMinute, TimeSpan.FromMinutes(1)) || !hits.TryHit("error-log:hour", MaxPerHour, TimeSpan.FromHours(1)))
                return; // the log file still has it

            await documents.InsertAsync(DocTypeName, new Dictionary<string, object?>
            {
                ["source"] = Cut(source, 140),
                ["method"] = Cut(method, 16),
                ["path"] = Cut(PathOnly(path), 300),
                ["user"] = Cut(user, 140),
                ["error_type"] = Cut(error.GetType().Name, 140),
                ["message"] = Cut(error.Message, MaxMessage),
                ["trace"] = Cut(error.ToString(), MaxTrace),
            }, System, CancellationToken.None);
        }
        catch (Exception)
        {
            // Recording a problem must never cause another one.
        }
    }

    /// <summary>The address without the query string or fragment, which may carry tokens or personal data.</summary>
    public static string? PathOnly(string? path)
    {
        if (path is null)
            return null;
        var end = path.IndexOfAny(['?', '#']);
        return end >= 0 ? path[..end] : path;
    }

    private static string? Cut(string? text, int length) =>
        text is null ? null : text.Length > length ? text[..length] : text;
}
