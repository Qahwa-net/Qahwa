using System.Globalization;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <param name="RemindAt">When to remind, as an ISO 8601 date and time with its offset, e.g. 2026-12-01T09:00:00+03:00 (a time without an offset is read as UTC).</param>
/// <param name="Message">An optional note, up to 500 characters.</param>
public sealed record ReminderRequest(string? RemindAt = null, string? Message = null);

public sealed record ReminderInfo(string Id, string DocType, string Name, DateTime RemindAt, string? Message, string Status, DateTime? SentAt);

/// <summary>
/// Reminders: a person asks to be told about a document at a time. At that time the scheduler puts a notification in their
/// inbox (and pushes it live if they are online). The usual notification rules still apply: the person must still be able to
/// read the document when the time comes, otherwise nothing is said. A reminder is claimed with a compare-and-swap on its
/// row before the notification is written, so two servers never send it twice.
/// </summary>
public sealed class ReminderService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    NotificationService notifications,
    HistoryRecorder history)
{
    public const string DocTypeName = "Reminder";
    public const int MaxPending = 100;
    public const int MaxMessage = 500;
    public const int MaxDaysAhead = 366;
    private const int MaxBatch = 200;
    private const string SystemUser = "system@qahwa";

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    private static ReminderInfo ToInfo(Dictionary<string, object?> row) => new(
        (string)row["name"]!, (string)row["reference_doctype"]!, (string)row["reference_name"]!, (DateTime)row["remind_at"]!,
        row.GetValueOrDefault("message") as string, row.GetValueOrDefault("status") as string ?? "Pending", row.GetValueOrDefault("sent_at") as DateTime?);

    /// <summary>Sets a reminder on a document the person can read.</summary>
    public async Task<ReminderInfo> CreateAsync(string doctype, string name, ReminderRequest request, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!await notifications.CanReadAsync(user, doctype, name, ct))
            throw new NotFoundException($"{doctype} '{name}' was not found.");

        if (!DateTimeOffset.TryParse(request.RemindAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            throw Invalid("remindAt", "Use a date and time like 2026-12-01T09:00:00+03:00.");
        var when = at.UtcDateTime;
        var now = history.Now();
        if (when <= now)
            throw Invalid("remindAt", "The time must be in the future.");
        if (when > now.AddDays(MaxDaysAhead))
            throw Invalid("remindAt", $"The time can be at most {MaxDaysAhead} days ahead.");

        var message = request.Message?.Trim();
        if (message is { Length: > MaxMessage })
            throw Invalid("message", $"The note can have at most {MaxMessage} characters.");

        var pending = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("status", FilterOp.Eq, "Pending")],
            Fields = ["name"], Limit = MaxPending + 1,
        }, ct);
        if (pending.Count >= MaxPending)
            throw Invalid(string.Empty, $"You can have at most {MaxPending} reminders waiting.");

        var row = HistoryRecorder.Standard(user.User, now);
        row["reference_doctype"] = doctype;
        row["reference_name"] = name;
        row["remind_at"] = when;
        row["message"] = string.IsNullOrEmpty(message) ? null : message;
        row["status"] = "Pending";
        await repository.InsertAsync(Meta, row, null, ct);
        return ToInfo(row);
    }

    /// <summary>The caller's reminders for one document, soonest first.</summary>
    public async Task<IReadOnlyList<ReminderInfo>> ForDocumentAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name), new("status", FilterOp.Ne, "Cancelled")],
            OrderBy = "remind_at", Descending = false, Limit = 50,
        }, ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <summary>The caller's reminders that have not been sent yet, soonest first.</summary>
    public async Task<IReadOnlyList<ReminderInfo>> ListPendingAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("status", FilterOp.Eq, "Pending")],
            OrderBy = "remind_at", Descending = false, Limit = MaxPending,
        }, ct);
        return rows.Select(ToInfo).ToList();
    }

    /// <summary>Takes back a reminder that has not been sent. Only its owner can; to anyone else it does not exist.</summary>
    public async Task CancelAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        if (row is null || !string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal))
            throw new NotFoundException($"Reminder '{id}' was not found.");
        if (row["status"] as string != "Pending")
            throw new ConflictException("This reminder has already been sent or cancelled.");
        var modified = (DateTime)row["modified"]!;
        var change = new Dictionary<string, object?> { ["status"] = "Cancelled", ["modified"] = Next(modified) };
        if (!await repository.UpdateAsync(Meta, id, change, modified, null, ct))
            throw new ConflictException("This reminder was just changed. Reload and try again.");
    }

    /// <summary>Sends every reminder that is due. Called by the scheduler; safe on several servers at once.</summary>
    /// <returns>How many people were told, and how many reminders were due but had nobody to tell (the person is gone or can no longer read the document).</returns>
    public async Task<(int Sent, int Dropped)> RunDueAsync(CancellationToken ct = default)
    {
        var now = history.Now();
        var due = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.Eq, "Pending"), new("remind_at", FilterOp.Le, now)],
            OrderBy = "remind_at", Descending = false, Limit = MaxBatch,
        }, ct);

        int sent = 0, dropped = 0;
        foreach (var row in due)
        {
            var id = (string)row["name"]!;
            var modified = (DateTime)row["modified"]!;
            // Claim it first: a second server that loses the swap leaves the reminder alone.
            var claim = new Dictionary<string, object?> { ["status"] = "Sent", ["sent_at"] = now, ["modified"] = Next(modified) };
            if (!await repository.UpdateAsync(Meta, id, claim, modified, null, ct))
                continue;

            var message = row["message"] as string;
            var doctype = (string)row["reference_doctype"]!;
            var name = (string)row["reference_name"]!;
            var told = await notifications.NotifyAsync((string)row["owner"]!, "Reminder", doctype, name, SystemUser,
                string.IsNullOrEmpty(message) ? $"Reminder: {doctype} {name}" : $"Reminder: {message}", ct);
            if (told)
                sent++;
            else
                dropped++;
        }

        return (sent, dropped);
    }
}
