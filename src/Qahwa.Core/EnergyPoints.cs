using System.Globalization;
using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed record EnergyPointEntry(string Reason, int Points, string? DocType, string? Name, DateTime At);

public sealed record EnergyPointSummary(int Total, int ThisWeek, int ThisMonth, IReadOnlyList<EnergyPointEntry> Recent);

/// <param name="You">True for the person asking. Only names are shown, never email addresses.</param>
public sealed record LeaderboardEntry(string FullName, int Points, bool You);

/// <summary>
/// Energy points (ADR-159): recognition for work done in the system. A rule says that when a document of some DocType is created, changed,
/// submitted or cancelled (optionally only when a condition holds) the person named by one of its fields gets some points. Points are written
/// to a log that cannot be edited, each person's total is the sum of their log, and a leaderboard ranks people over a week, a month or all
/// time. A rule can award once for each document and at most so many times a day to one person, so points cannot be farmed by editing.
/// </summary>
public sealed class EnergyPointService(IMetaRegistry registry, IDocumentRepository repository, DocumentService documents, TimeProvider clock)
{
    public const string RuleDocType = "Energy Point Rule";
    public const string LogDocType = "Energy Point Log";
    public const int MaxPoints = 100;
    private const int MaxRulesPerEvent = 10;
    private const int MaxLogRows = 20_000;
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);
    private static readonly AsyncLocal<bool> Running = new();

    private sealed record Snapshot(DateTime At, ILookup<string, Dictionary<string, object?>> Rules);

    private volatile Snapshot? _snapshot;

    public void Invalidate() => _snapshot = null;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    /// <summary>The rules a rule must meet: a real DocType, a sensible number of points, and a user field that exists.</summary>
    public static void Validate(IMetaRegistry registry, IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        if (values.GetValueOrDefault("points") is { } raw && (Convert.ToInt64(raw, CultureInfo.InvariantCulture) is 0 or > MaxPoints or < -MaxPoints))
            errors.Add(new FieldError("points", $"Use from -{MaxPoints} to {MaxPoints} points, not zero."));
        if (values.GetValueOrDefault("max_per_day") is { } day && Convert.ToInt64(day, CultureInfo.InvariantCulture) is < 0 or > 1000)
            errors.Add(new FieldError("max_per_day", "Use 0 (no limit) or a number up to 1000."));
        if (values.GetValueOrDefault("reference_doctype") is not string doctype)
            return;
        if (!registry.TryGet(doctype, out var meta) || meta.IsChildTable || meta.IsSingle)
        {
            errors.Add(new FieldError("reference_doctype", $"'{doctype}' is not a DocType with documents of its own."));
            return;
        }

        if (values.GetValueOrDefault("user_field") is string who && who.Length > 0 && who is not ("owner" or "modified_by")
            && !meta.Fields.Any(f => f.Name == who && f.Type == FieldType.Link && f.Options == "User"))
            errors.Add(new FieldError("user_field", $"{doctype} has no Link-to-User field named '{who}'. Use owner, modified_by or a Link field to User."));
        if (values.GetValueOrDefault("event") as string == "ValueChange"
            && (values.GetValueOrDefault("watch_field") is not string watch || !meta.Fields.Any(f => f.Name == watch)))
            errors.Add(new FieldError("watch_field", "Name a field of the DocType to watch."));
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> RulesForAsync(string doctype, CancellationToken ct)
    {
        var now = Now();
        var snapshot = _snapshot;
        if (snapshot is null || now - snapshot.At > CacheFor)
        {
            var rows = await repository.ListAsync(registry.Get(RuleDocType), new ListQuery { Filters = [new("enabled", FilterOp.Eq, true)], OrderBy = "creation", Descending = false, Limit = 1000 }, ct);
            snapshot = new Snapshot(now, rows.ToLookup(r => r["reference_doctype"] as string ?? string.Empty, StringComparer.Ordinal));
            _snapshot = snapshot;
        }

        return snapshot.Rules[doctype].ToList();
    }

    private static HashSet<string> ChangedFields(DocTypeMeta meta, IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after) =>
        meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && !Equals(Convert.ToString(before.GetValueOrDefault(f.Name), CultureInfo.InvariantCulture), Convert.ToString(after.GetValueOrDefault(f.Name), CultureInfo.InvariantCulture)))
            .Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Called after a document was saved: awards the points of every rule that fits. A problem here never undoes the save.</summary>
    public async Task AfterSaveAsync(DocTypeMeta meta, string docName, bool isNew, IReadOnlyDictionary<string, object?>? before, IReadOnlyDictionary<string, object?> after, IUserContext actor, CancellationToken ct = default)
    {
        if (Running.Value || meta.Name is LogDocType or RuleDocType || !registry.TryGet(RuleDocType, out _))
            return;
        Running.Value = true;
        try
        {
            var rules = await RulesForAsync(meta.Name, ct);
            if (rules.Count == 0)
                return;
            var changed = before is null ? [] : ChangedFields(meta, before, after);
            var oldStatus = Convert.ToInt32(before?.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            var newStatus = Convert.ToInt32(after.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            foreach (var rule in rules.Take(MaxRulesPerEvent))
            {
                var applies = (rule["event"] as string) switch
                {
                    "New" => isNew,
                    "Update" => !isNew && changed.Count > 0,
                    "ValueChange" => !isNew && rule["watch_field"] is string watch && changed.Contains(watch),
                    "Submit" => !isNew && oldStatus == 0 && newStatus == 1,
                    "Cancel" => !isNew && oldStatus == 1 && newStatus == 2,
                    _ => false,
                };
                if (!applies || !documents.EvaluateCondition(rule["condition"] as string, meta, after, actor))
                    continue;
                await AwardAsync(meta, docName, rule, after, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is saved; a problem with recognition must not turn that into an error.
        }
        finally
        {
            Running.Value = false;
        }
    }

    private async Task AwardAsync(DocTypeMeta meta, string docName, Dictionary<string, object?> rule, IReadOnlyDictionary<string, object?> doc, CancellationToken ct)
    {
        var userField = rule.GetValueOrDefault("user_field") as string is { Length: > 0 } f ? f : "owner";
        if (doc.GetValueOrDefault(userField) as string is not { Length: > 0 } user || !registry.TryGet("User", out var userMeta))
            return;
        var person = await repository.GetAsync(userMeta, user, ct);
        if (person is null || person.GetValueOrDefault("enabled") is false)
            return; // nobody to give it to: not a person of this site, or an account that is switched off

        var log = registry.Get(LogDocType);
        var ruleName = (string)rule["name"]!;
        var points = Convert.ToInt32(rule["points"], CultureInfo.InvariantCulture);
        if (rule.GetValueOrDefault("once_per_document") is not false
            && await repository.CountAsync(log, new ListQuery { Filters = [new("rule", FilterOp.Eq, ruleName), new("reference_doctype", FilterOp.Eq, meta.Name), new("reference_name", FilterOp.Eq, docName), new("user", FilterOp.Eq, user)] }, ct) > 0)
            return;
        var perDay = Convert.ToInt32(rule.GetValueOrDefault("max_per_day") ?? 0, CultureInfo.InvariantCulture);
        if (perDay > 0 && await repository.CountAsync(log, new ListQuery { Filters = [new("rule", FilterOp.Eq, ruleName), new("user", FilterOp.Eq, user), new("creation", FilterOp.Ge, Now().Date)] }, ct) >= perDay)
            return;

        var row = HistoryRecorder.Standard("Administrator", Now());
        row["user"] = user;
        row["points"] = (long)points;
        row["reason"] = $"{ruleName}: {meta.Name} {docName}";
        row["rule"] = ruleName;
        row["reference_doctype"] = meta.Name;
        row["reference_name"] = docName;
        await repository.InsertAsync(log, row, null, ct);
    }

    private async Task<List<Dictionary<string, object?>>> LogSinceAsync(DateTime? since, string? user, CancellationToken ct)
    {
        var filters = new List<QueryFilter>();
        if (since is { } from)
            filters.Add(new QueryFilter("creation", FilterOp.Ge, from));
        if (user is not null)
            filters.Add(new QueryFilter("user", FilterOp.Eq, user));
        return (await repository.ListAsync(registry.Get(LogDocType), new ListQuery { Filters = filters, OrderBy = "creation", Descending = true, Limit = MaxLogRows }, ct)).ToList();
    }

    private static int Sum(IEnumerable<Dictionary<string, object?>> rows) => rows.Sum(r => Convert.ToInt32(r["points"] ?? 0, CultureInfo.InvariantCulture));

    /// <summary>One person's points: the total, this week and month, and the latest entries.</summary>
    public async Task<EnergyPointSummary> MineAsync(IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var rows = await LogSinceAsync(null, user.User, ct);
        var now = Now();
        return new EnergyPointSummary(
            Sum(rows), Sum(rows.Where(r => (DateTime)r["creation"]! >= WeekStart(now))), Sum(rows.Where(r => (DateTime)r["creation"]! >= new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc))),
            rows.Take(20).Select(r => new EnergyPointEntry(r["reason"] as string ?? string.Empty, Convert.ToInt32(r["points"], CultureInfo.InvariantCulture), r["reference_doctype"] as string, r["reference_name"] as string, (DateTime)r["creation"]!)).ToList());
    }

    private static DateTime WeekStart(DateTime now) => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7)); // Monday

    /// <summary>The people with the most points over "week", "month" or "all" (names only, top 20; people at zero or below are left out).</summary>
    public async Task<IReadOnlyList<LeaderboardEntry>> LeaderboardAsync(string? period, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var now = Now();
        DateTime? since = period switch
        {
            "week" => WeekStart(now),
            "month" => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            null or "all" => null,
            _ => throw new DocumentValidationException([new FieldError("period", "Use week, month or all.")]),
        };
        var totals = (await LogSinceAsync(since, null, ct)).GroupBy(r => (string)r["user"]!).Select(g => (User: g.Key, Points: Sum(g)))
            .Where(t => t.Points > 0).OrderByDescending(t => t.Points).ThenBy(t => t.User, StringComparer.Ordinal).Take(20).ToList();
        var userMeta = registry.Get("User");
        var board = new List<LeaderboardEntry>();
        foreach (var (who, points) in totals)
        {
            var row = await repository.GetAsync(userMeta, who, ct);
            var name = row?.GetValueOrDefault("full_name") as string;
            board.Add(new LeaderboardEntry(string.IsNullOrWhiteSpace(name) ? "Someone" : name, points, string.Equals(who, user.User, StringComparison.Ordinal)));
        }

        return board;
    }
}
