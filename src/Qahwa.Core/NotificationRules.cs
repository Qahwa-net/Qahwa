using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>One rule: when something happens to a document, tell some people something.</summary>
public sealed record NotificationRule
{
    public const string New = "New";
    public const string Update = "Update";
    public const string ValueChange = "ValueChange";
    public const string Submit = "Submit";
    public const string Cancel = "Cancel";
    public const string DaysBefore = "DaysBefore";
    public const string DaysAfter = "DaysAfter";
    public static readonly string[] Events = [New, Update, ValueChange, Submit, Cancel, DaysBefore, DaysAfter];

    public const int MaxRules = 20;
    public const int MaxRecipients = 50;
    public const int MaxCondition = 500;
    public const int MaxMessage = 300;

    public required string Name { get; init; }
    public bool Active { get; init; } = true;

    /// <summary>New, Update, ValueChange, Submit, Cancel, DaysBefore or DaysAfter.</summary>
    public string Event { get; init; } = New;

    /// <summary>ValueChange: the field whose change triggers the rule.</summary>
    public string? Field { get; init; }

    /// <summary>DaysBefore and DaysAfter: the Date or Datetime field counted from.</summary>
    public string? DateField { get; init; }

    /// <summary>DaysBefore and DaysAfter: how many days before or after the date (0 to 365).</summary>
    public int? Days { get; init; }

    /// <summary>The rule only applies while this expression over the document's fields is true; empty means always.</summary>
    public string? Condition { get; init; }

    public IReadOnlyList<string> Users { get; init; } = [];
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>Fields of the document that hold a user (a Link to User, or a Data or Email field).</summary>
    public IReadOnlyList<string> UserFields { get; init; } = [];

    public bool NotifyOwner { get; init; }
    public bool NotifyFollowers { get; init; }

    /// <summary>Also send the message by email to the people told (only those who were notified, so only people who may read the document).</summary>
    public bool Email { get; init; }

    /// <summary>What the notification says. <c>{{field}}</c> is replaced by the document's value; also <c>{{name}}</c>, <c>{{doctype}}</c> and <c>{{user}}</c> (who did it).</summary>
    public string Message { get; init; } = string.Empty;
}

public sealed record NotificationRuleSet
{
    public required string DocType { get; init; }
    public IReadOnlyList<NotificationRule> Rules { get; init; } = [];
}

public sealed record StoredNotificationRules(string DocType, string Definition, DateTime Modified, string ModifiedBy);

public interface INotificationRuleStore
{
    Task<IReadOnlyList<StoredNotificationRules>> ListAsync(CancellationToken ct = default);

    Task<StoredNotificationRules?> GetAsync(string docType, CancellationToken ct = default);

    /// <param name="expectedModified">Null when the DocType has no rules yet.</param>
    Task<bool> SaveAsync(StoredNotificationRules row, DateTime? expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string docType, CancellationToken ct = default);
}

public sealed class NotificationRuleCatalog
{
    private ImmutableDictionary<string, NotificationRuleSet> _sets = ImmutableDictionary.Create<string, NotificationRuleSet>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<NotificationRuleSet> All => _sets.Values.ToList();

    public NotificationRuleSet? For(string doctype) => _sets.GetValueOrDefault(doctype);

    internal void Set(NotificationRuleSet set)
    {
        if (set.Rules.Any(r => r.Active))
            ImmutableInterlocked.AddOrUpdate(ref _sets, set.DocType, set, (_, _) => set);
        else
            ImmutableInterlocked.TryRemove(ref _sets, set.DocType, out _);
    }

    internal void Remove(string doctype) => ImmutableInterlocked.TryRemove(ref _sets, doctype, out _);
}

public sealed record NotificationRuleItem(string DocType, string? Module, DocTypeSource Source, bool HasRules, int Rules, int ActiveRules);

public sealed record NotificationRuleView(
    string DocType,
    DocTypeSource Source,
    bool Exists,
    DateTime? Modified,
    NotificationRuleSet Set,
    IReadOnlyList<string> UserFields,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> DateFields,
    bool CanEdit);

/// <summary>Fills <c>{{field}}</c> in a message. Only plain field names are replaced: there is no code, no formatting language.</summary>
public static partial class MessageTemplate
{
    public static readonly string[] Specials = ["name", "doctype", "user"];
    private const int MaxValue = 200;

    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex Placeholder();

    /// <summary>The names used in the template.</summary>
    public static IReadOnlyList<string> Names(string template) =>
        Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Anything left that looks like a placeholder but is not a plain name (<c>{{ a.b }}</c>, <c>{{1+1}}</c>).</summary>
    public static bool HasMalformed(string template) => Placeholder().Replace(template, string.Empty).Contains("{{", StringComparison.Ordinal);

    public static string Render(string template, Func<string, object?> value) =>
        Placeholder().Replace(template, m => Text(value(m.Groups[1].Value)));

    private static string Text(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            bool b => b ? "Yes" : "No",
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        text = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        return text.Length > MaxValue ? text[..MaxValue] + "…" : text;
    }
}

/// <summary>
/// The Notification Rules page: say who is told what when something happens to a document. Rules are data, validated against
/// the DocType and applied at once. Managing them needs a System Manager.
/// </summary>
public sealed class NotificationRuleManager(
    IMetaRegistry registry,
    INotificationRuleStore store,
    NotificationRuleCatalog catalog,
    IDocumentRepository repository,
    PermissionService permissions,
    TimeProvider clock)
{
    private readonly SemaphoreSlim _changes = new(1, 1);

    private bool CanManage(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!CanManage(user))
            throw new PermissionDeniedException("Only a System Manager can manage notification rules.");
    }

    private DocTypeMeta Workable(string doctype, out DocTypeSource source)
    {
        if (!registry.TryGet(doctype, out var meta) || !AssignmentRuleManager.Applies(meta))
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        source = registry.SourceOf(doctype)!.Value;
        return meta;
    }

    /// <summary>Fields whose value can be named in a message or watched for changes: real columns, not tables or layout.</summary>
    public static IReadOnlyList<string> ValueFieldsOf(DocTypeMeta meta) =>
        meta.Fields.Where(f => FieldTypes.HasColumn(f.Type) && f.Type != FieldType.Password).Select(f => f.Name).ToList();

    public static IReadOnlyList<string> DateFieldsOf(DocTypeMeta meta) =>
        meta.Fields.Where(f => f.Type is FieldType.Date or FieldType.Datetime).Select(f => f.Name).ToList();

    public async Task<IReadOnlyList<NotificationRuleItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var stored = (await store.ListAsync(ct)).ToDictionary(r => r.DocType, StringComparer.OrdinalIgnoreCase);
        return registry.All.Where(AssignmentRuleManager.Applies).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var set = stored.TryGetValue(m.Name, out var row) ? TryParse(row.Definition) : null;
                return new NotificationRuleItem(m.Name, m.Module, registry.SourceOf(m.Name)!.Value, row is not null, set?.Rules.Count ?? 0, set?.Rules.Count(r => r.Active) ?? 0);
            }).ToList();
    }

    private static NotificationRuleSet? TryParse(string json)
    {
        try
        {
            return DocTypeLoader.ParseNotificationRules(json);
        }
        catch (DocTypeValidationException)
        {
            return null;
        }
    }

    public async Task<NotificationRuleView> GetAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = Workable(doctype, out var source);
        var stored = await store.GetAsync(doctype, ct);
        var set = (stored is null ? null : TryParse(stored.Definition)) ?? new NotificationRuleSet { DocType = doctype };
        return new NotificationRuleView(doctype, source, stored is not null, stored?.Modified, set, AssignmentRuleManager.UserFieldsOf(meta), ValueFieldsOf(meta), DateFieldsOf(meta), CanManage(user));
    }

    public async Task<NotificationRuleView> SaveAsync(
        string doctype, NotificationRuleSet set, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            var meta = Workable(doctype, out _);
            set = Clean(set with { DocType = doctype });
            var errors = await ValidateAsync(set, meta, ct);
            if (errors.Count > 0)
                throw new DocumentValidationException(errors);

            var stored = await store.GetAsync(doctype, ct);
            if (stored?.Modified != expectedModified)
                throw new ConflictException($"The notification rules of '{doctype}' were changed by someone else. Reload and try again.");

            var utc = clock.GetUtcNow().UtcDateTime;
            var row = new StoredNotificationRules(doctype, DocTypeLoader.SerializeNotificationRules(set), new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc), user.User);
            if (!await store.SaveAsync(row, expectedModified, ct))
                throw new ConflictException($"The notification rules of '{doctype}' were changed by someone else. Reload and try again.");

            catalog.Set(set);
            return await GetAsync(doctype, user, ct);
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task DeleteAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            Workable(doctype, out _);
            await store.DeleteAsync(doctype, ct);
            catalog.Remove(doctype);
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                var set = DocTypeLoader.ParseNotificationRules(row.Definition);
                var meta = Workable(row.DocType, out _);
                var errors = ValidateShape(Clean(set), meta);
                if (errors.Count > 0)
                    throw new DocTypeValidationException(string.Join(" ", errors.Select(e => $"{e.Field}: {e.Message}")));
                catalog.Set(set);
            }
            catch (Exception ex) when (ex is DocTypeValidationException or NotFoundException)
            {
                failures.Add($"The notification rules of '{row.DocType}' were skipped: {ex.Message}");
            }
        }

        return failures;
    }

    private static List<string> Clean(IReadOnlyList<string>? list) =>
        (list ?? []).Select(u => u?.Trim() ?? string.Empty).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    private static NotificationRuleSet Clean(NotificationRuleSet input) => input with
    {
        Rules = (input.Rules ?? []).Select(r => r with
        {
            Name = r.Name?.Trim() ?? string.Empty,
            Event = r.Event?.Trim() ?? string.Empty,
            Field = string.IsNullOrWhiteSpace(r.Field) ? null : r.Field.Trim(),
            DateField = string.IsNullOrWhiteSpace(r.DateField) ? null : r.DateField.Trim(),
            Condition = string.IsNullOrWhiteSpace(r.Condition) ? null : r.Condition.Trim(),
            Users = Clean(r.Users),
            Roles = Clean(r.Roles),
            UserFields = Clean(r.UserFields),
            Message = r.Message?.Trim() ?? string.Empty,
        }).ToList(),
    };

    private async Task<List<FieldError>> ValidateAsync(NotificationRuleSet set, DocTypeMeta meta, CancellationToken ct)
    {
        var errors = ValidateShape(set, meta);
        registry.TryGet("User", out var userMeta);
        registry.TryGet("Role", out var roleMeta);
        for (var i = 0; i < set.Rules.Count; i++)
        {
            if (userMeta is not null)
            {
                foreach (var user in set.Rules[i].Users.Take(NotificationRule.MaxRecipients).Where(u => u.Length <= FieldTypes.ShortTextLength))
                {
                    if (!await repository.ExistsAsync(userMeta, user, ct))
                        errors.Add(new FieldError($"rules[{i}].users", $"'{user}' is not a user."));
                }
            }

            if (roleMeta is not null)
            {
                foreach (var role in set.Rules[i].Roles.Take(NotificationRule.MaxRecipients).Where(r => r.Length <= FieldTypes.ShortTextLength))
                {
                    if (!await repository.ExistsAsync(roleMeta, role, ct))
                        errors.Add(new FieldError($"rules[{i}].roles", $"'{role}' is not a role."));
                }
            }
        }

        return errors;
    }

    /// <summary>Every problem with a rule set that can be found without looking at users and roles, keyed by path.</summary>
    public static List<FieldError> ValidateShape(NotificationRuleSet set, DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        void Add(string path, string message) => errors.Add(new FieldError(path, message));

        if (set.Rules.Count > NotificationRule.MaxRules)
            Add("rules", $"At most {NotificationRule.MaxRules} rules.");

        var known = meta.Fields.Select(f => f.Name).Concat(StandardFields.Columns.Keys).ToHashSet(StringComparer.Ordinal);
        var values = ValueFieldsOf(meta).Concat(StandardFields.Columns.Keys).Concat(MessageTemplate.Specials).ToHashSet(StringComparer.Ordinal);
        var userFields = AssignmentRuleManager.UserFieldsOf(meta);
        var dateFields = DateFieldsOf(meta);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < set.Rules.Count; i++)
        {
            var r = set.Rules[i];
            if (r.Name.Length is 0 or > 100 || r.Name.Any(char.IsControl))
                Add($"rules[{i}].name", "Give the rule a name of up to 100 characters.");
            else if (!names.Add(r.Name))
                Add($"rules[{i}].name", $"There is already a rule named '{r.Name}'.");

            if (!NotificationRule.Events.Contains(r.Event))
                Add($"rules[{i}].event", $"Use one of: {string.Join(", ", NotificationRule.Events)}.");
            if (r.Event == NotificationRule.ValueChange && (r.Field is null || !ValueFieldsOf(meta).Contains(r.Field)))
                Add($"rules[{i}].field", "Choose the field whose change triggers this rule.");
            if (r.Event is NotificationRule.DaysBefore or NotificationRule.DaysAfter)
            {
                if (r.DateField is null || !dateFields.Contains(r.DateField))
                    Add($"rules[{i}].dateField", "Choose a Date or Datetime field.");
                if (r.Days is null or < 0 or > 365)
                    Add($"rules[{i}].days", "Use a number of days from 0 to 365.");
            }

            if (r.Condition is { } condition)
            {
                if (condition.Length > NotificationRule.MaxCondition)
                    Add($"rules[{i}].condition", $"At most {NotificationRule.MaxCondition} characters.");
                else
                {
                    try
                    {
                        var unknown = Expression.Parse(condition).Identifiers.FirstOrDefault(n => !known.Contains(n));
                        if (unknown is not null)
                            Add($"rules[{i}].condition", $"'{unknown}' is not a field of this DocType.");
                    }
                    catch (ExpressionException ex)
                    {
                        Add($"rules[{i}].condition", ex.Message);
                    }
                }
            }

            if (r.Users.Count + r.Roles.Count + r.UserFields.Count == 0 && !r.NotifyOwner && !r.NotifyFollowers)
                Add($"rules[{i}].users", "Say who to tell: people, roles, a user field, the owner or the followers.");
            if (r.Users.Count > NotificationRule.MaxRecipients || r.Roles.Count > NotificationRule.MaxRecipients)
                Add($"rules[{i}].users", $"At most {NotificationRule.MaxRecipients} people and {NotificationRule.MaxRecipients} roles.");
            if (r.Users.Concat(r.Roles).Any(n => n.Length > FieldTypes.ShortTextLength))
                Add($"rules[{i}].users", "A name is too long.");
            if (r.UserFields.Any(f => !userFields.Contains(f)))
                Add($"rules[{i}].userFields", "Use fields that hold a user (a Link to User, or a Data or Email field).");

            if (r.Message.Length == 0 || r.Message.Length > NotificationRule.MaxMessage || r.Message.Any(char.IsControl))
                Add($"rules[{i}].message", $"Write what to tell them, in up to {NotificationRule.MaxMessage} characters on one line.");
            else if (MessageTemplate.HasMalformed(r.Message))
                Add($"rules[{i}].message", "Use {{field}} with a plain field name.");
            else if (MessageTemplate.Names(r.Message).FirstOrDefault(n => !values.Contains(n)) is { } bad)
                Add($"rules[{i}].message", $"'{bad}' is not a field of this DocType.");
        }

        return errors;
    }
}

/// <summary>
/// Applies notification rules: after a document is saved (new, changed, a value changed, submitted, cancelled) and, from the
/// scheduler, a number of days before or after a date. Notifications go to the in-app bell, only to people who may read the
/// document, and a rule that cannot run never fails a save.
/// </summary>
public sealed class NotificationRuleEngine(
    NotificationRuleCatalog catalog,
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    NotificationService notifications,
    FollowService follows,
    EmailService email,
    TimeProvider clock)
{
    public const int MaxRecipients = 200;
    private const int ScanLimit = 1000;
    private static readonly UserContext System = new("Administrator", [UserContext.AdministratorRole]);

    public async Task AfterSaveAsync(
        DocTypeMeta meta,
        string docName,
        bool isNew,
        IReadOnlyDictionary<string, object?>? before,
        IReadOnlyDictionary<string, object?> after,
        IUserContext actor,
        CancellationToken ct = default)
    {
        if (!AssignmentRuleManager.Applies(meta) || catalog.For(meta.Name) is not { } set)
            return;

        try
        {
            var changed = before is null ? [] : ChangedFields(meta, before, after);
            var oldStatus = Convert.ToInt32(before?.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            var newStatus = Convert.ToInt32(after.GetValueOrDefault("docstatus") ?? 0, CultureInfo.InvariantCulture);
            foreach (var rule in set.Rules.Where(r => r.Active))
            {
                var applies = rule.Event switch
                {
                    NotificationRule.New => isNew,
                    NotificationRule.Update => !isNew && changed.Count > 0,
                    NotificationRule.ValueChange => !isNew && rule.Field is not null && changed.Contains(rule.Field),
                    NotificationRule.Submit => !isNew && oldStatus == 0 && newStatus == 1,
                    NotificationRule.Cancel => !isNew && oldStatus == 1 && newStatus == 2,
                    _ => false,
                };
                if (applies && documents.EvaluateCondition(rule.Condition, meta, after, actor))
                    await SendAsync(meta, docName, rule, after, actor, ruleKey: null, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is already saved; telling people is best effort and must not turn that into an error.
        }
    }

    /// <summary>Notifications for rules counted in days from a date. Safe to call often and from several servers: each person is told once per rule, document and day.</summary>
    public async Task<int> RunScheduledAsync(CancellationToken ct = default)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var sent = 0;
        foreach (var set in catalog.All)
        {
            if (!registry.TryGet(set.DocType, out var meta))
                continue;
            foreach (var rule in set.Rules.Where(r => r.Active && r.Event is NotificationRule.DaysBefore or NotificationRule.DaysAfter && r.DateField is not null))
            {
                try
                {
                    var target = rule.Event == NotificationRule.DaysBefore ? today.AddDays(rule.Days ?? 0) : today.AddDays(-(rule.Days ?? 0));
                    sent += await ScanAsync(meta, rule, target, today, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One broken rule does not stop the others; it is tried again on the next run.
                }
            }
        }

        return sent;
    }

    private async Task<int> ScanAsync(DocTypeMeta meta, NotificationRule rule, DateOnly target, DateOnly today, CancellationToken ct)
    {
        var field = meta.Fields.First(f => f.Name == rule.DateField);
        var filters = field.Type == FieldType.Date
            ? new List<QueryFilter> { new(field.Name, FilterOp.Eq, target) }
            : [new(field.Name, FilterOp.Ge, target.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)), new(field.Name, FilterOp.Lt, target.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc))];
        filters.Add(new QueryFilter("docstatus", FilterOp.Ne, 2));
        var rows = await repository.ListAsync(meta, new ListQuery { Filters = filters, OrderBy = "name", Descending = false, Limit = ScanLimit }, ct);

        var sent = 0;
        foreach (var row in rows)
        {
            var doc = await repository.GetAsync(meta, (string)row["name"]!, ct);
            if (doc is not null && documents.EvaluateCondition(rule.Condition, meta, doc, System))
                sent += await SendAsync(meta, (string)doc["name"]!, rule, doc, System, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct);
        }

        return sent;
    }

    private async Task<int> SendAsync(
        DocTypeMeta meta, string docName, NotificationRule rule, IReadOnlyDictionary<string, object?> doc, IUserContext actor, string? ruleKey, CancellationToken ct)
    {
        var who = await notifications.NameOfAsync(actor.User, ct) ?? actor.User;
        var subject = MessageTemplate.Render(rule.Message, name => name switch
        {
            "name" => docName,
            "doctype" => meta.Name,
            "user" => who,
            _ => doc.GetValueOrDefault(name),
        });

        var told = new List<string>();
        foreach (var person in await RecipientsAsync(meta, docName, rule, doc, ct))
        {
            if (await notifications.NotifyAsync(person, "Alert", meta.Name, docName, actor.User, subject, ct, rule.Name, ruleKey))
                told.Add(person);
        }

        // Users are named by their email address. Anyone who could not be told (no access, or already told today) gets no email either.
        var addresses = told.Where(EmailService.ValidAddress).ToList();
        if (rule.Email && addresses.Count > 0)
            await email.EnqueueAsync(addresses, subject, $"{subject}\n\n{meta.Name} {docName}", meta.Name, docName, "Administrator", ct, MaxRecipients);
        return told.Count;
    }

    private async Task<IReadOnlyList<string>> RecipientsAsync(DocTypeMeta meta, string docName, NotificationRule rule, IReadOnlyDictionary<string, object?> doc, CancellationToken ct)
    {
        var people = new List<string>(rule.Users);
        foreach (var field in rule.UserFields)
        {
            if (doc.GetValueOrDefault(field) is string { Length: > 0 } value)
                people.Add(value);
        }

        if (rule.NotifyOwner && doc.GetValueOrDefault("owner") is string owner)
            people.Add(owner);
        if (rule.NotifyFollowers)
            people.AddRange(await follows.FollowersAsync(meta.Name, docName, ct));
        if (rule.Roles.Count > 0 && registry.TryGet("Has Role", out var hasRole))
        {
            var rows = await repository.ListAsync(hasRole, new ListQuery
            {
                Filters = [new("role", FilterOp.In, rule.Roles.Cast<object?>().ToList())],
                Fields = ["user"], OrderBy = "name", Descending = false, Limit = MaxRecipients,
            }, ct);
            people.AddRange(rows.Select(r => (string)r["user"]!));
        }

        return people.Distinct(StringComparer.Ordinal).Take(MaxRecipients).ToList();
    }

    private static HashSet<string> ChangedFields(DocTypeMeta meta, IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
        {
            if (!Same(before.GetValueOrDefault(field.Name), after.GetValueOrDefault(field.Name)))
                changed.Add(field.Name);
        }

        return changed;
    }

    private static bool Same(object? a, object? b)
    {
        if (a is null or "")
            return b is null or "";
        if (b is null or "")
            return false;
        if (a is IConvertible && b is IConvertible && a is not string && b is not string && a is not DateTime && b is not DateTime)
            return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        return Equals(a, b);
    }
}
