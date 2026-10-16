using System.Globalization;
using System.Net.Mail;
using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

/// <param name="Report">The id of a saved report (see <see cref="ReportService"/>).</param>
/// <param name="Recipients">Email addresses, one per line or separated by commas.</param>
/// <param name="Frequency">Daily, Weekly or Monthly.</param>
/// <param name="SendHour">The hour (0 to 23, UTC) the email is sent.</param>
/// <param name="RunAs">The person whose permissions decide what the report contains (default: whoever sets this up).</param>
public sealed record AutoEmailReportRequest(
    string? Report, string? Recipients, string? Frequency, int? SendHour, string? RunAs = null, int? MaxRows = null, string? Subject = null, string? Status = null);

public sealed record AutoEmailReportInfo(
    string Id, string Report, string? ReportTitle, IReadOnlyList<string> Recipients, string Frequency, int SendHour, DateTime? NextSend, string Status,
    string RunAs, int MaxRows, string? Subject, DateTime? LastSent, long SentCount, string? LastError);

/// <summary>
/// Auto Email Report: a saved report is run on a schedule and its first rows are emailed as a plain-text table. It is run as one named
/// person, so their permissions decide what the table holds. Only a System Manager can set these up (a report sent to other people is
/// data leaving the system). The scheduler calls <see cref="RunDueAsync"/>; each occurrence is claimed with a compare-and-swap on the
/// row, so two servers never send the same one twice, and a missed occurrence is sent once, not once per day missed.
/// </summary>
public sealed class AutoEmailReportService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    ReportService reports,
    EmailService email,
    NotificationService notifications,
    PermissionService permissions,
    HistoryRecorder history)
{
    public const string DocTypeName = "Auto Email Report";
    public const int MaxRecipients = 10;
    public const int MaxRowsLimit = 200;
    public const int DefaultMaxRows = 50;
    public const int MaxTotal = 100;
    private const int MaxBatch = 100;
    private const int MaxError = 500;
    private const int MaxCell = 60;

    public static readonly string[] Frequencies = ["Daily", "Weekly", "Monthly"];

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private IUserContext RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!IsManager(user))
            throw new PermissionDeniedException("Only a System Manager can set up emailed reports.");
        return user;
    }

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    /// <summary>The time after <paramref name="after"/> at which a report on this schedule is next due (always in the future).</summary>
    public static DateTime NextSlot(DateTime after, string frequency, int hour, DateTime? previousSlot = null)
    {
        var slot = previousSlot ?? new DateTime(after.Year, after.Month, after.Day, hour, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < 1000 && slot <= after; i++)
        {
            slot = frequency switch
            {
                "Weekly" => slot.AddDays(7),
                "Monthly" => slot.AddMonths(1),
                _ => slot.AddDays(1),
            };
        }

        return slot;
    }

    private static IReadOnlyList<string> ParseRecipients(string? text)
    {
        var list = (text ?? string.Empty).Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.Count is 0 or > MaxRecipients)
            throw Invalid("recipients", $"Give between 1 and {MaxRecipients} email addresses.");
        foreach (var address in list)
        {
            if (address.Length > 254 || !MailAddress.TryCreate(address, out var parsed) || parsed.Address != address || address.Any(char.IsControl) || address.Contains(' '))
                throw Invalid("recipients", $"'{new string(address.Where(c => !char.IsControl(c)).Take(60).ToArray())}' is not an email address.");
        }

        return list;
    }

    private async Task<AutoEmailReportInfo> ToInfoAsync(Dictionary<string, object?> row, CancellationToken ct)
    {
        var report = (string)row.GetValueOrDefault("report")!;
        string? title = null;
        var reportMeta = registry.Get(ReportService.DocTypeName);
        if (await repository.GetAsync(reportMeta, report, ct) is { } r)
            title = r["title"] as string;
        return new AutoEmailReportInfo(
            (string)row.GetValueOrDefault("name")!, report, title, ParseRecipients((string?)row.GetValueOrDefault("recipients")), row.GetValueOrDefault("frequency") as string ?? "Daily",
            Convert.ToInt32(row.GetValueOrDefault("send_hour") ?? 0, CultureInfo.InvariantCulture), row.GetValueOrDefault("next_send") as DateTime?, row.GetValueOrDefault("status") as string ?? "Active",
            (string)row.GetValueOrDefault("run_as")!, Convert.ToInt32(row.GetValueOrDefault("max_rows") ?? DefaultMaxRows, CultureInfo.InvariantCulture), row.GetValueOrDefault("subject") as string,
            row.GetValueOrDefault("last_sent") as DateTime?, Convert.ToInt64(row.GetValueOrDefault("sent_count") ?? 0L, CultureInfo.InvariantCulture), row.GetValueOrDefault("last_error") as string);
    }

    public async Task<IReadOnlyList<AutoEmailReportInfo>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var rows = await repository.ListAsync(Meta, new ListQuery { OrderBy = "creation", Descending = false, Limit = MaxTotal }, ct);
        var list = new List<AutoEmailReportInfo>();
        foreach (var row in rows)
            list.Add(await ToInfoAsync(row, ct));
        return list;
    }

    private async Task<Dictionary<string, object?>> RequireRowAsync(string id, CancellationToken ct) =>
        await repository.GetAsync(Meta, id, ct) ?? throw new NotFoundException($"Emailed report '{id}' was not found.");

    private async Task<UserContext> RunAsContextAsync(string runAs, IUserContext actor, CancellationToken ct)
    {
        // Always a real, enabled user from the user list, with the roles it holds there: the same person the scheduler will run it as.
        return await notifications.ContextOfAsync(runAs, ct) ?? throw Invalid("runAs", $"'{runAs}' is not an enabled user.");
    }

    private async Task<Dictionary<string, object?>> RowFromAsync(AutoEmailReportRequest request, IUserContext actor, Dictionary<string, object?>? existing, CancellationToken ct)
    {
        var reportId = (request.Report ?? existing?.GetValueOrDefault("report") as string ?? string.Empty).Trim();
        var recipients = ParseRecipients(request.Recipients ?? existing?.GetValueOrDefault("recipients") as string);
        var frequency = request.Frequency ?? existing?.GetValueOrDefault("frequency") as string ?? "Daily";
        if (!Frequencies.Contains(frequency))
            throw Invalid("frequency", "Use Daily, Weekly or Monthly.");
        var hour = request.SendHour ?? (existing is null ? 8 : Convert.ToInt32(existing.GetValueOrDefault("send_hour"), CultureInfo.InvariantCulture));
        if (hour is < 0 or > 23)
            throw Invalid("sendHour", "The hour must be between 0 and 23 (UTC).");
        var maxRows = request.MaxRows ?? (existing is null ? DefaultMaxRows : Convert.ToInt32(existing.GetValueOrDefault("max_rows"), CultureInfo.InvariantCulture));
        if (maxRows is < 1 or > MaxRowsLimit)
            throw Invalid("maxRows", $"Send between 1 and {MaxRowsLimit} rows.");
        var status = request.Status ?? existing?.GetValueOrDefault("status") as string ?? "Active";
        if (status is not ("Active" or "Paused"))
            throw Invalid("status", "Use Active or Paused.");
        var subject = string.IsNullOrWhiteSpace(request.Subject) ? null : request.Subject.Trim();
        if (subject is { Length: > EmailService.MaxSubject } || subject?.Any(char.IsControl) == true)
            throw Invalid("subject", $"Write a subject of up to {EmailService.MaxSubject} characters.");
        var runAs = (request.RunAs ?? existing?.GetValueOrDefault("run_as") as string ?? actor.User).Trim();

        // The report must exist and be one the chosen person may run: a report is never sent with someone else's wider permissions.
        var context = await RunAsContextAsync(runAs, actor, ct);
        try
        {
            await reports.GetAsync(reportId, context, ct);
        }
        catch (Exception ex) when (ex is NotFoundException or PermissionDeniedException)
        {
            throw Invalid("report", "That report was not found, or the person it runs as cannot open it.");
        }

        var now = history.Now();
        var changedSchedule = existing is null || frequency != (string?)existing.GetValueOrDefault("frequency") || hour != Convert.ToInt32(existing.GetValueOrDefault("send_hour"), CultureInfo.InvariantCulture);
        return new Dictionary<string, object?>
        {
            ["report"] = reportId, ["recipients"] = string.Join('\n', recipients), ["frequency"] = frequency, ["send_hour"] = (long)hour,
            ["max_rows"] = (long)maxRows, ["status"] = status, ["run_as"] = runAs, ["subject"] = subject,
            ["next_send"] = changedSchedule || existing?.GetValueOrDefault("next_send") is null ? NextSlot(now, "Daily", hour) : existing.GetValueOrDefault("next_send"), // the first one is the next time that hour comes round
        };
    }

    public async Task<AutoEmailReportInfo> CreateAsync(AutoEmailReportRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        if (await repository.CountAsync(Meta, new ListQuery(), ct) >= MaxTotal)
            throw Invalid("report", $"At most {MaxTotal} emailed reports can be set up.");
        var values = await RowFromAsync(request, user, null, ct);
        var row = HistoryRecorder.Standard(user.User, history.Now());
        foreach (var (key, value) in values)
            row[key] = value;
        row["sent_count"] = 0L;
        await repository.InsertAsync(Meta, row, null, ct);
        return await ToInfoAsync(row, ct);
    }

    public async Task<AutoEmailReportInfo> UpdateAsync(string id, AutoEmailReportRequest request, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var existing = await RequireRowAsync(id, ct);
        var values = await RowFromAsync(request, user, existing, ct);
        values["modified"] = Next((DateTime)existing.GetValueOrDefault("modified")!);
        values["modified_by"] = user.User;
        if (!await repository.UpdateAsync(Meta, id, values, (DateTime)existing.GetValueOrDefault("modified")!, null, ct))
            throw new ConflictException("This emailed report was changed by someone else. Reload and try again.");
        return await ToInfoAsync((await repository.GetAsync(Meta, id, ct))!, ct);
    }

    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await RequireRowAsync(id, ct);
        await repository.DeleteAsync(Meta, id, null, ct);
    }

    /// <summary>Sends the report now, as well as on its schedule. Returns how many emails were queued.</summary>
    public async Task<int> SendNowAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var row = await RequireRowAsync(id, ct);
        var queued = await SendAsync(row, ct);
        await repository.UpdateAsync(Meta, id, new Dictionary<string, object?>
        {
            ["last_sent"] = history.Now(), ["last_error"] = null, ["sent_count"] = Convert.ToInt64(row.GetValueOrDefault("sent_count") ?? 0L, CultureInfo.InvariantCulture) + 1,
            ["modified"] = Next((DateTime)row.GetValueOrDefault("modified")!),
        }, (DateTime)row.GetValueOrDefault("modified")!, null, ct);
        return queued;
    }

    /// <summary>Sends every report that is due. Called by the scheduler; safe to call from several servers at once.</summary>
    public async Task<(int Sent, int Failed)> RunDueAsync(CancellationToken ct = default)
    {
        var now = history.Now();
        var due = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.Eq, "Active"), new("next_send", FilterOp.Le, now)],
            OrderBy = "next_send", Descending = false, Limit = MaxBatch,
        }, ct);

        int sent = 0, failed = 0;
        foreach (var row in due)
        {
            var id = (string)row.GetValueOrDefault("name")!;
            var version = (DateTime)row.GetValueOrDefault("modified")!;
            var slot = NextSlot(now, (string)row.GetValueOrDefault("frequency")!, Convert.ToInt32(row.GetValueOrDefault("send_hour"), CultureInfo.InvariantCulture), (DateTime)row.GetValueOrDefault("next_send")!);
            // Claim this occurrence before sending, so a second server cannot send it too.
            if (!await repository.UpdateAsync(Meta, id, new Dictionary<string, object?> { ["next_send"] = slot, ["modified"] = Next(version) }, version, null, ct))
                continue;

            var outcome = new Dictionary<string, object?>();
            try
            {
                await SendAsync(row, ct);
                outcome["last_sent"] = history.Now();
                outcome["last_error"] = null;
                outcome["sent_count"] = Convert.ToInt64(row.GetValueOrDefault("sent_count") ?? 0L, CultureInfo.InvariantCulture) + 1;
                sent++;
            }
            catch (Exception ex) when (ex is DocumentValidationException or PermissionDeniedException or NotFoundException or UnauthenticatedException)
            {
                var message = ex is DocumentValidationException v ? string.Join(" ", v.Errors.Select(e => e.Message)) : ex.Message;
                outcome["last_error"] = message.Length > MaxError ? message[..MaxError] : message;
                failed++;
            }

            var fresh = await repository.GetAsync(Meta, id, ct);
            if (fresh is not null)
            {
                outcome["modified"] = Next((DateTime)fresh["modified"]!);
                await repository.UpdateAsync(Meta, id, outcome, (DateTime)fresh["modified"]!, null, ct);
            }
        }

        return (sent, failed);
    }

    private async Task<int> SendAsync(Dictionary<string, object?> row, CancellationToken ct)
    {
        var runAs = (string)row.GetValueOrDefault("run_as")!;
        var context = await notifications.ContextOfAsync(runAs, ct)
            ?? throw new PermissionDeniedException($"'{runAs}' is no longer an enabled user, so the report cannot be run as them.");
        var (columns, rows, truncated, title) = await reports.ExportAsync((string)row.GetValueOrDefault("report")!, context, ct);
        var maxRows = Convert.ToInt32(row.GetValueOrDefault("max_rows") ?? DefaultMaxRows, CultureInfo.InvariantCulture);
        var body = Format(title, columns, rows, maxRows, truncated);
        var subject = row.GetValueOrDefault("subject") as string is { Length: > 0 } s ? s : title;
        return await email.EnqueueAsync(ParseRecipients((string?)row.GetValueOrDefault("recipients")), subject, body, null, null, "Administrator", ct, MaxRecipients);
    }

    /// <summary>The report as plain text: a title, a header line and the first rows, cut to fit one email.</summary>
    public static string Format(string title, IReadOnlyList<string> columns, IReadOnlyList<Dictionary<string, object?>> rows, int maxRows, bool truncated)
    {
        var text = new StringBuilder();
        text.Append(title).Append('\n').Append('\n');
        text.Append(string.Join(" | ", columns.Select(c => Cell(c)))).Append('\n');
        text.Append(new string('-', Math.Min(60, Math.Max(10, columns.Count * 8)))).Append('\n');
        var shown = 0;
        foreach (var row in rows.Take(maxRows))
        {
            var line = string.Join(" | ", columns.Select(c => Cell(Show(row.GetValueOrDefault(c)))));
            if (text.Length + line.Length + 200 > EmailService.MaxBody)
                break;
            text.Append(line).Append('\n');
            shown++;
        }

        text.Append('\n');
        text.Append(shown < rows.Count || truncated
            ? $"Showing the first {shown} of {(truncated ? "more than " : string.Empty)}{rows.Count} rows. Open the report in Qahwa to see all of them."
            : $"{shown} row{(shown == 1 ? string.Empty : "s")}.");
        return text.ToString();
    }

    private static string Show(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "Yes" : "No",
        DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Cell(string text)
    {
        var line = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length > MaxCell ? line[..MaxCell] + "…" : line;
    }
}
