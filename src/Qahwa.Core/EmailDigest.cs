using System.Globalization;
using System.Net.Mail;
using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

public sealed record EmailDigestSettings(bool Enabled, string Frequency, int SendHour, DateTime? NextSend, DateTime? LastSent);

public sealed record EmailDigestRequest(bool? Enabled = null, string? Frequency = null, int? SendHour = null);

/// <summary>
/// Email Digest: a person who turns it on gets one email a day (or a week) with their unread notifications and open assignments, sent
/// to their own address and nowhere else. Nothing is sent when there is nothing to say. Each person manages only their own digest.
/// </summary>
public sealed class EmailDigestService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    NotificationService notifications,
    AssignmentService assignments,
    EmailService email,
    HistoryRecorder history)
{
    public const string DocTypeName = "Email Digest";
    public const int MaxNotifications = 30;
    public const int MaxAssignments = 20;
    private const int MaxBatch = 200;
    private const int MaxLine = 200;

    public static readonly string[] Frequencies = ["Daily", "Weekly"];

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private static EmailDigestSettings Settings(Dictionary<string, object?>? row) => row is null
        ? new EmailDigestSettings(false, "Daily", 7, null, null)
        : new EmailDigestSettings(
            row.GetValueOrDefault("enabled") is true or 1 or 1L or "1", row.GetValueOrDefault("frequency") as string ?? "Daily",
            Convert.ToInt32(row.GetValueOrDefault("send_hour") ?? 7, CultureInfo.InvariantCulture), row.GetValueOrDefault("next_send") as DateTime?, row.GetValueOrDefault("last_sent") as DateTime?);

    public async Task<EmailDigestSettings> GetAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        return Settings(await repository.GetAsync(Meta, user.User, ct));
    }

    public async Task<EmailDigestSettings> SetAsync(IUserContext user, EmailDigestRequest request, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!MailAddress.TryCreate(user.User, out var parsed) || parsed.Address != user.User)
            throw Invalid("enabled", "Your account has no email address to send a digest to.");

        var existing = await repository.GetAsync(Meta, user.User, ct);
        var current = Settings(existing);
        var enabled = request.Enabled ?? current.Enabled;
        var frequency = request.Frequency ?? current.Frequency;
        var hour = request.SendHour ?? current.SendHour;
        if (!Frequencies.Contains(frequency))
            throw Invalid("frequency", "Use Daily or Weekly.");
        if (hour is < 0 or > 23)
            throw Invalid("sendHour", "The hour must be between 0 and 23 (UTC).");

        var now = history.Now();
        var reschedule = enabled && (!current.Enabled || current.NextSend is null || frequency != current.Frequency || hour != current.SendHour);
        var values = new Dictionary<string, object?>
        {
            ["enabled"] = enabled, ["frequency"] = frequency, ["send_hour"] = (long)hour,
            ["next_send"] = !enabled ? null : reschedule ? AutoEmailReportService.NextSlot(now, "Daily", hour) : current.NextSend,
        };
        if (existing is null)
        {
            var row = HistoryRecorder.Standard(user.User, now);
            row["name"] = user.User;
            row["user"] = user.User;
            foreach (var (key, value) in values)
                row[key] = value;
            await repository.InsertAsync(Meta, row, null, ct);
        }
        else
        {
            values["modified"] = Next((DateTime)existing["modified"]!);
            values["modified_by"] = user.User;
            if (!await repository.UpdateAsync(Meta, user.User, values, (DateTime)existing["modified"]!, null, ct))
                throw new ConflictException("Your digest settings were changed somewhere else. Try again.");
        }

        return Settings(await repository.GetAsync(Meta, user.User, ct));
    }

    /// <summary>Sends every digest that is due. Called by the scheduler; safe to call from several servers at once.</summary>
    public async Task<(int Sent, int Skipped)> RunDueAsync(CancellationToken ct = default)
    {
        var now = history.Now();
        var due = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("enabled", FilterOp.Eq, true), new("next_send", FilterOp.Le, now)],
            OrderBy = "next_send", Descending = false, Limit = MaxBatch,
        }, ct);

        int sent = 0, skipped = 0;
        foreach (var row in due)
        {
            var person = (string)row["user"]!;
            var version = (DateTime)row["modified"]!;
            var frequency = row.GetValueOrDefault("frequency") as string ?? "Daily";
            var next = AutoEmailReportService.NextSlot(now, frequency, Convert.ToInt32(row.GetValueOrDefault("send_hour") ?? 7, CultureInfo.InvariantCulture), (DateTime)row["next_send"]!);
            // Claim this occurrence before sending, so a second server cannot send it too.
            if (!await repository.UpdateAsync(Meta, person, new Dictionary<string, object?> { ["next_send"] = next, ["modified"] = Next(version) }, version, null, ct))
                continue;

            var context = await notifications.ContextOfAsync(person, ct);
            if (context is null)
            {
                skipped++; // no longer an enabled user
                continue;
            }

            var body = await BodyAsync(context, ct);
            if (body is null)
            {
                skipped++;
                continue;
            }

            await email.EnqueueAsync([person], frequency == "Weekly" ? "Your weekly Qahwa digest" : "Your daily Qahwa digest", body, null, null, "Administrator", ct);
            var fresh = await repository.GetAsync(Meta, person, ct);
            if (fresh is not null)
                await repository.UpdateAsync(Meta, person, new Dictionary<string, object?> { ["last_sent"] = history.Now(), ["modified"] = Next((DateTime)fresh["modified"]!) }, (DateTime)fresh["modified"]!, null, ct);
            sent++;
        }

        return (sent, skipped);
    }

    private static string Line(string? text)
    {
        var line = new string((text ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return line.Length > MaxLine ? line[..MaxLine] + "…" : line;
    }

    /// <summary>The text of the digest, or null when there is nothing to tell this person.</summary>
    private async Task<string?> BodyAsync(UserContext person, CancellationToken ct)
    {
        var unread = await notifications.ListAsync(person, unreadOnly: true, MaxNotifications, before: null, ct);
        var open = await assignments.MineAsync(person, "Open", ct);
        if (unread.Count == 0 && open.Count == 0)
            return null;

        var text = new StringBuilder();
        if (unread.Count > 0)
        {
            text.Append(unread.Count == MaxNotifications ? $"Unread notifications (the latest {MaxNotifications}):\n" : $"Unread notifications ({unread.Count}):\n");
            foreach (var item in unread)
                text.Append("  - ").Append(Line(item.Subject)).Append(" (").Append(Line(item.DocType)).Append(' ').Append(Line(item.DocName)).Append(")\n");
            text.Append('\n');
        }

        if (open.Count > 0)
        {
            text.Append($"Open assignments ({open.Count}):\n");
            foreach (var item in open.Take(MaxAssignments))
            {
                text.Append("  - ").Append(Line(item.DocType)).Append(' ').Append(Line(item.DocName));
                if (!string.IsNullOrWhiteSpace(item.Description))
                    text.Append(": ").Append(Line(item.Description));
                if (item.Due is not null)
                    text.Append(" (due ").Append(Line(item.Due)).Append(')');
                text.Append('\n');
            }

            if (open.Count > MaxAssignments)
                text.Append($"  … and {open.Count - MaxAssignments} more.\n");
            text.Append('\n');
        }

        text.Append("Open Qahwa to see everything. You can turn this email off in your account settings.");
        return text.ToString();
    }
}
