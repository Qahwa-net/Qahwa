using System.Globalization;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>When the copies of a repeating document are due: counted from the start date, so months never drift.</summary>
public static class AutoRepeatSchedule
{
    public static readonly string[] Frequencies = ["Daily", "Weekly", "Monthly", "Quarterly", "Yearly"];

    /// <summary>The date of occurrence <paramref name="n"/> (0 is the start date itself).</summary>
    public static DateOnly On(DateOnly start, string frequency, long n) => frequency switch
    {
        "Daily" => start.AddDays(checked((int)n)),
        "Weekly" => start.AddDays(checked((int)(7 * n))),
        "Monthly" => start.AddMonths(checked((int)n)),
        "Quarterly" => start.AddMonths(checked((int)(3 * n))),
        "Yearly" => start.AddYears(checked((int)n)),
        _ => throw new ArgumentOutOfRangeException(nameof(frequency)),
    };
}

/// <param name="Frequency">Daily, Weekly, Monthly, Quarterly or Yearly.</param>
/// <param name="StartDate">The first date a copy is made (yyyy-MM-dd, not in the past).</param>
/// <param name="EndDate">No copies after this date (yyyy-MM-dd); empty means never.</param>
/// <param name="DateFields">Date or Datetime fields of the document that are set to the date of each copy.</param>
/// <param name="Status">Active or Paused.</param>
public sealed record AutoRepeatRequest(
    string? Frequency = null, string? StartDate = null, string? EndDate = null, IReadOnlyList<string>? DateFields = null, string? Status = null);

public sealed record AutoRepeatInfo(
    string Id,
    string DocType,
    string Name,
    string Frequency,
    string StartDate,
    string? EndDate,
    string? NextDate,
    string Status,
    IReadOnlyList<string> DateFields,
    long Created,
    DateTime? LastRun,
    string? LastCreated,
    string? LastError,
    string Owner,
    string? OwnerName);

public sealed record AutoRepeatRun(int Created, int Failed);

/// <summary>
/// Auto Repeat: a document that makes a fresh draft copy of itself every day, week, month, quarter or year. The copies are
/// made as the person who set the repeat up, so their permissions decide what may be copied. The scheduler (a background
/// loop in the server) calls <see cref="RunDueAsync"/>; each due occurrence is claimed with a compare-and-swap on the row,
/// so two servers running at once never make the same copy twice.
/// </summary>
public sealed class AutoRepeatService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    NotificationService notifications,
    HistoryRecorder history)
{
    public const string DocTypeName = "Auto Repeat";
    public const int MaxPerDocument = 5;
    public const int MaxPerUser = 100;
    public const int MaxDateFields = 10;
    public const int MaxCatchUp = 5;
    private const int MaxBatch = 200;
    private const int MaxError = 500;

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    /// <summary>A row version later than <paramref name="previous"/> even when the clock has not moved (see <see cref="EmailService"/>).</summary>
    private DateTime Next(DateTime previous) => history.Now() > previous ? history.Now() : previous.AddTicks(10);

    private DateOnly Today() => DateOnly.FromDateTime(history.Now());

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private static string Fmt(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private static DateOnly? DateOf(object? value) => value switch
    {
        DateOnly d => d,
        DateTime dt => DateOnly.FromDateTime(dt),
        _ => null,
    };

    private async Task<AutoRepeatInfo> ToInfoAsync(Dictionary<string, object?> row, CancellationToken ct)
    {
        var owner = (string)row["owner"]!;
        return new AutoRepeatInfo(
            (string)row["name"]!, (string)row["reference_doctype"]!, (string)row["reference_name"]!,
            row["frequency"] as string ?? "Daily", DateOf(row["start_date"]) is { } s ? Fmt(s) : string.Empty,
            DateOf(row["end_date"]) is { } e ? Fmt(e) : null, DateOf(row["next_date"]) is { } n ? Fmt(n) : null,
            row["status"] as string ?? "Active",
            (row["date_fields"] as string ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Convert.ToInt64(row["created_count"] ?? 0L, CultureInfo.InvariantCulture), row["last_run"] as DateTime?, row["last_created"] as string,
            row["last_error"] as string, owner, await notifications.NameOfAsync(owner, ct));
    }

    private async Task<Dictionary<string, object?>> RequireAsync(string id, IUserContext user, CancellationToken ct)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await repository.GetAsync(Meta, id, ct);
        // Only whoever set it up (or a System Manager) can see that it exists.
        return row is not null && (IsManager(user) || string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal))
            ? row
            : throw new NotFoundException("Auto repeat not found.");
    }

    public async Task<IReadOnlyList<AutoRepeatInfo>> ForDocumentAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        await documents.GetAsync(doctype, name, user, ct); // reading the document is the least it takes
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            OrderBy = "creation", Descending = false, Limit = 50,
        }, ct);
        var result = new List<AutoRepeatInfo>();
        foreach (var row in rows.Where(r => IsManager(user) || string.Equals(r["owner"] as string, user.User, StringComparison.Ordinal)))
            result.Add(await ToInfoAsync(row, ct));
        return result;
    }

    /// <summary>The caller's repeats (a System Manager sees everyone's), soonest first.</summary>
    public async Task<IReadOnlyList<AutoRepeatInfo>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        List<QueryFilter> filters = IsManager(user) ? [] : [new("owner", FilterOp.Eq, user.User)];
        var rows = await repository.ListAsync(Meta, new ListQuery { Filters = filters, OrderBy = "next_date", Descending = false, Limit = MaxBatch }, ct);
        var result = new List<AutoRepeatInfo>();
        foreach (var row in rows)
            result.Add(await ToInfoAsync(row, ct));
        return result;
    }

    private DateOnly ParseDate(string field, string? text)
    {
        if (!DateOnly.TryParseExact((text ?? string.Empty).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw Invalid(field, "Use a date like 2026-12-31.");
        return date;
    }

    private static string CleanDateFields(DocTypeMeta meta, IReadOnlyList<string>? requested)
    {
        var names = (requested ?? []).Select(n => n?.Trim() ?? string.Empty).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (names.Count > MaxDateFields)
            throw Invalid("dateFields", $"At most {MaxDateFields} fields.");
        foreach (var name in names)
        {
            if (meta.Fields.FirstOrDefault(f => f.Name == name) is not { Type: FieldType.Date or FieldType.Datetime })
                throw Invalid("dateFields", $"'{name}' is not a Date or Datetime field of {meta.Name}.");
        }

        return string.Join(',', names);
    }

    public async Task<AutoRepeatInfo> CreateAsync(string doctype, string name, AutoRepeatRequest request, IUserContext user, CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        if (meta.IsChildTable || meta.IsSingle || meta.Module == "Core" || meta.Autoname == "prompt")
            throw Invalid(string.Empty, "This kind of document cannot be repeated.");
        await documents.GetAsync(doctype, name, user, ct); // proves it exists and may be read
        if (permissions.Evaluate(meta, user, PermAction.Create) == AccessLevel.None)
            throw new PermissionDeniedException($"You may not create {meta.Name} documents, so you cannot repeat one.");

        var frequency = request.Frequency?.Trim() ?? string.Empty;
        if (!AutoRepeatSchedule.Frequencies.Contains(frequency))
            throw Invalid("frequency", $"Use one of: {string.Join(", ", AutoRepeatSchedule.Frequencies)}.");
        var start = ParseDate("startDate", request.StartDate);
        if (start < Today())
            throw Invalid("startDate", "The start date cannot be in the past.");
        DateOnly? end = string.IsNullOrWhiteSpace(request.EndDate) ? null : ParseDate("endDate", request.EndDate);
        if (end is { } last && last < start)
            throw Invalid("endDate", "The end date cannot be before the start date.");
        var dateFields = CleanDateFields(meta, request.DateFields);

        var mine = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("owner", FilterOp.Eq, user.User), new("status", FilterOp.Ne, "Finished")],
            Fields = ["reference_doctype", "reference_name"], OrderBy = "creation", Descending = false, Limit = MaxPerUser + 1,
        }, ct);
        if (mine.Count >= MaxPerUser)
            throw Invalid(string.Empty, $"You can have at most {MaxPerUser} repeating documents.");
        if (mine.Count(r => (string)r["reference_doctype"]! == doctype && (string)r["reference_name"]! == name) >= MaxPerDocument)
            throw Invalid(string.Empty, $"A document can repeat in at most {MaxPerDocument} ways.");

        var row = HistoryRecorder.Standard(user.User, history.Now());
        row["reference_doctype"] = doctype;
        row["reference_name"] = name;
        row["frequency"] = frequency;
        row["start_date"] = start;
        row["end_date"] = end;
        row["next_date"] = start;
        row["occurrence"] = 0L;
        row["status"] = "Active";
        row["date_fields"] = dateFields.Length == 0 ? null : dateFields;
        row["created_count"] = 0L;
        await repository.InsertAsync(Meta, row, null, ct);
        return await ToInfoAsync((await repository.GetAsync(Meta, (string)row["name"]!, ct))!, ct);
    }

    /// <summary>Changes the end date, the date fields, or pauses and resumes a repeat. The frequency and start date are fixed: make a new repeat instead.</summary>
    public async Task<AutoRepeatInfo> UpdateAsync(string id, AutoRepeatRequest request, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        var meta = registry.Get((string)row["reference_doctype"]!);
        var changes = new Dictionary<string, object?> { ["modified"] = Next((DateTime)row["modified"]!), ["modified_by"] = user.User };

        if (request.EndDate is not null)
        {
            DateOnly? end = request.EndDate.Trim().Length == 0 ? null : ParseDate("endDate", request.EndDate);
            if (end is { } last && last < DateOf(row["start_date"]))
                throw Invalid("endDate", "The end date cannot be before the start date.");
            changes["end_date"] = end;
        }

        if (request.DateFields is not null)
            changes["date_fields"] = CleanDateFields(meta, request.DateFields) is { Length: > 0 } fields ? fields : null;

        if (request.Status is not null)
        {
            if (request.Status is not ("Active" or "Paused"))
                throw Invalid("status", "Use Active or Paused.");
            if (row["status"] as string == "Finished")
                throw Invalid("status", "This repeat has finished. Make a new one.");
            changes["status"] = request.Status;
            if (request.Status == "Active")
                changes["last_error"] = null;
        }

        if (!await repository.UpdateAsync(Meta, id, changes, (DateTime)row["modified"]!, null, ct))
            throw new ConflictException("This repeat was changed by someone else. Reload and try again.");
        return await ToInfoAsync((await repository.GetAsync(Meta, id, ct))!, ct);
    }

    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        await RequireAsync(id, user, ct);
        await repository.DeleteAsync(Meta, id, null, ct);
    }

    /// <summary>Makes the next copy now instead of waiting. It uses up that occurrence: the schedule carries on after it.</summary>
    public async Task<AutoRepeatInfo> RunNowAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        var row = await RequireAsync(id, user, ct);
        if (row["status"] as string != "Active")
            throw Invalid("status", "Only an active repeat can make a copy.");
        await RunRowAsync(row, Today(), force: true, ct);
        return await ToInfoAsync((await repository.GetAsync(Meta, id, ct))!, ct);
    }

    /// <summary>Makes every copy that is due. Called by the scheduler; safe to call from several servers at once.</summary>
    public async Task<AutoRepeatRun> RunDueAsync(CancellationToken ct = default)
    {
        var today = Today();
        var due = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("status", FilterOp.Eq, "Active"), new("next_date", FilterOp.Le, today)],
            OrderBy = "next_date", Descending = false, Limit = MaxBatch,
        }, ct);

        int created = 0, failed = 0;
        foreach (var row in due)
        {
            var (made, bad) = await RunRowAsync(row, today, force: false, ct);
            created += made;
            failed += bad;
        }

        return new AutoRepeatRun(created, failed);
    }

    private async Task<(int Created, int Failed)> RunRowAsync(Dictionary<string, object?> row, DateOnly today, bool force, CancellationToken ct)
    {
        int created = 0, failed = 0;
        var id = (string)row["name"]!;
        for (var i = 0; i < (force ? 1 : MaxCatchUp); i++)
        {
            if (DateOf(row["next_date"]) is not { } next || (!force && next > today))
                break;
            var start = DateOf(row["start_date"])!.Value;
            var frequency = (string)row["frequency"]!;
            var end = DateOf(row["end_date"]);
            var occurrence = Convert.ToInt64(row["occurrence"] ?? 0L, CultureInfo.InvariantCulture);
            if (end is { } over && next > over)
            {
                await repository.UpdateAsync(Meta, id, new Dictionary<string, object?> { ["status"] = "Finished", ["modified"] = Next((DateTime)row["modified"]!) }, (DateTime)row["modified"]!, null, ct);
                break;
            }

            // Claim the occurrence before making the copy, so a second server cannot make it too.
            var following = AutoRepeatSchedule.On(start, frequency, occurrence + 1);
            var claim = new Dictionary<string, object?>
            {
                ["occurrence"] = occurrence + 1, ["next_date"] = following, ["modified"] = Next((DateTime)row["modified"]!),
                ["status"] = end is { } last && following > last ? "Finished" : "Active",
            };
            if (!await repository.UpdateAsync(Meta, id, claim, (DateTime)row["modified"]!, null, ct))
                break;

            var outcome = new Dictionary<string, object?> { ["last_run"] = history.Now() };
            try
            {
                var name = await CopyAsync(row, force ? today : next, ct);
                outcome["last_created"] = name;
                outcome["last_error"] = null;
                outcome["created_count"] = Convert.ToInt64(row["created_count"] ?? 0L, CultureInfo.InvariantCulture) + 1;
                created++;
                await notifications.NotifyAsync((string)row["owner"]!, "Update", (string)row["reference_doctype"]!, name, "Administrator",
                    $"Auto repeat made {(string)row["reference_doctype"]!} {name}", ct);
            }
            catch (Exception ex) when (ex is DocumentValidationException or PermissionDeniedException or NotFoundException or ConflictException or FormatException)
            {
                // The schedule goes on; the failure is on the row, and the owner is told.
                var reason = ex is DocumentValidationException v ? string.Join(" ", v.Errors.Select(e => e.Message)) : ex.Message;
                outcome["last_error"] = reason.Length > MaxError ? reason[..MaxError] : reason;
                failed++;
                if (ex is PermissionDeniedException or NotFoundException)
                    outcome["status"] = "Paused"; // nothing will change by itself: wait for a person
            }

            outcome["modified"] = Next((DateTime)claim["modified"]!);
            await repository.UpdateAsync(Meta, id, outcome, null, null, ct);
            row = await repository.GetAsync(Meta, id, ct) ?? row;
            if (row["status"] as string != "Active")
                break;
        }

        return (created, failed);
    }

    /// <summary>Copies the source document as a new draft, owned by whoever set the repeat up.</summary>
    private async Task<string> CopyAsync(Dictionary<string, object?> repeat, DateOnly date, CancellationToken ct)
    {
        var owner = (string)repeat["owner"]!;
        var person = await notifications.ContextOfAsync(owner, ct) ?? throw new PermissionDeniedException("The person who set up this repeat is no longer an active user.");
        var doctype = (string)repeat["reference_doctype"]!;
        var meta = registry.Get(doctype);
        var source = await documents.GetAsync(doctype, (string)repeat["reference_name"]!, person, ct);
        var dateFields = (repeat["date_fields"] as string ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in meta.Fields.Where(CopyableField))
        {
            if (field.Type == FieldType.Table)
            {
                if (source.GetValueOrDefault(field.Name) is IEnumerable<object?> rows && registry.TryGet(field.Options ?? string.Empty, out var child))
                {
                    var copied = new List<Dictionary<string, object?>>();
                    foreach (var rowValues in rows.OfType<IReadOnlyDictionary<string, object?>>())
                    {
                        copied.Add(child.Fields.Where(CopyableField).Where(f => f.Type != FieldType.Table && rowValues.ContainsKey(f.Name))
                            .ToDictionary(f => f.Name, f => rowValues[f.Name], StringComparer.Ordinal));
                    }

                    values[field.Name] = copied;
                }
            }
            else if (dateFields.Contains(field.Name))
                values[field.Name] = field.Type == FieldType.Date ? date : date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            else if (source.TryGetValue(field.Name, out var value) && value is not null)
                values[field.Name] = value;
        }

        return (string)(await documents.InsertAsync(doctype, values, person, ct))["name"]!;
    }

    private static bool CopyableField(DocFieldMeta f) =>
        // Unique values (a reference number) would clash with the original, so they are left for the new document to get its own.
        f.Type is not (FieldType.SectionBreak or FieldType.ColumnBreak or FieldType.TabBreak or FieldType.Password) && f.Formula is null && !f.Unique;
}
