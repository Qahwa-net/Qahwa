using System.Collections.Immutable;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>One rule: when a document matches the condition, give it to someone by a strategy.</summary>
public sealed record AssignmentRule
{
    public const string RoundRobin = "RoundRobin";
    public const string LoadBalancing = "LoadBalancing";
    public const string Field = "Field";
    public static readonly string[] Strategies = [RoundRobin, LoadBalancing, Field];
    public static readonly string[] Priorities = ["Low", "Medium", "High"];

    public const int MaxRules = 20;
    public const int MaxUsers = 50;
    public const int MaxCondition = 500;
    public const int MaxDescription = 500;

    /// <summary>Names the rule in the Assignment Rules page and in the document's activity.</summary>
    public required string Name { get; init; }

    public bool Active { get; init; } = true;

    /// <summary>RoundRobin (people in turn), LoadBalancing (whoever has the fewest open assignments) or Field (the user named in a field).</summary>
    public string Strategy { get; init; } = RoundRobin;

    /// <summary>The people to choose from (RoundRobin and LoadBalancing).</summary>
    public IReadOnlyList<string> Users { get; init; } = [];

    /// <summary>The field that holds the user to assign (Field).</summary>
    public string? UserField { get; init; }

    /// <summary>The rule applies while this expression over the document's fields is true; empty means always.</summary>
    public string? Condition { get; init; }

    /// <summary>When true for a document the rule assigned, the assignment is withdrawn (for example <c>status = 'Closed'</c>).</summary>
    public string? CloseCondition { get; init; }

    public string? Description { get; init; }

    /// <summary>The assignment is due this many days after it is made.</summary>
    public int? DueInDays { get; init; }

    public string Priority { get; init; } = "Medium";
}

/// <summary>The rules of one DocType, tried in order.</summary>
public sealed record AssignmentRuleSet
{
    public required string DocType { get; init; }
    public IReadOnlyList<AssignmentRule> Rules { get; init; } = [];
}

public sealed record StoredAssignmentRules(string DocType, string Definition, DateTime Modified, string ModifiedBy);

public interface IAssignmentRuleStore
{
    Task<IReadOnlyList<StoredAssignmentRules>> ListAsync(CancellationToken ct = default);

    Task<StoredAssignmentRules?> GetAsync(string docType, CancellationToken ct = default);

    /// <param name="expectedModified">Null when the DocType has no rules yet.</param>
    Task<bool> SaveAsync(StoredAssignmentRules row, DateTime? expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string docType, CancellationToken ct = default);
}

/// <summary>The rules in force, as an immutable snapshot the document engine reads on every save.</summary>
public sealed class AssignmentRuleCatalog
{
    private ImmutableDictionary<string, AssignmentRuleSet> _sets = ImmutableDictionary.Create<string, AssignmentRuleSet>(StringComparer.OrdinalIgnoreCase);

    public AssignmentRuleSet? For(string doctype) => _sets.GetValueOrDefault(doctype);

    internal void Set(AssignmentRuleSet set)
    {
        if (set.Rules.Any(r => r.Active))
            ImmutableInterlocked.AddOrUpdate(ref _sets, set.DocType, set, (_, _) => set);
        else
            ImmutableInterlocked.TryRemove(ref _sets, set.DocType, out _);
    }

    internal void Remove(string doctype) => ImmutableInterlocked.TryRemove(ref _sets, doctype, out _);
}

public sealed record AssignmentRuleItem(string DocType, string? Module, DocTypeSource Source, bool HasRules, int Rules, int ActiveRules);

public sealed record AssignmentRuleView(
    string DocType,
    DocTypeSource Source,
    bool Exists,
    DateTime? Modified,
    AssignmentRuleSet Set,
    IReadOnlyList<string> UserFields,
    bool CanEdit);

/// <summary>
/// The Assignment Rules page: say who gets which documents. Rules are data, validated against the DocType and applied
/// at once. Managing them needs a System Manager; the framework's own DocTypes are not covered.
/// </summary>
public sealed class AssignmentRuleManager(
    IMetaRegistry registry,
    IAssignmentRuleStore store,
    AssignmentRuleCatalog catalog,
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
            throw new PermissionDeniedException("Only a System Manager can manage assignment rules.");
    }

    /// <summary>Rules apply to the DocTypes people work with: not child tables, settings or the framework's own.</summary>
    public static bool Applies(DocTypeMeta meta) => !meta.IsChildTable && !meta.IsSingle && meta.Module != "Core";

    private DocTypeMeta Workable(string doctype, out DocTypeSource source)
    {
        if (!registry.TryGet(doctype, out var meta) || !Applies(meta))
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        source = registry.SourceOf(doctype)!.Value;
        return meta;
    }

    /// <summary>The fields that can name a user: Link fields to User, and Data or Email fields.</summary>
    public static IReadOnlyList<string> UserFieldsOf(DocTypeMeta meta) =>
        meta.Fields.Where(f => f.Type is FieldType.Data or FieldType.Email || (f.Type == FieldType.Link && f.Options == "User"))
            .Where(f => f.Formula is null).Select(f => f.Name).ToList();

    public async Task<IReadOnlyList<AssignmentRuleItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var stored = (await store.ListAsync(ct)).ToDictionary(r => r.DocType, StringComparer.OrdinalIgnoreCase);
        return registry.All.Where(Applies).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var set = stored.TryGetValue(m.Name, out var row) ? TryParse(row.Definition) : null;
                return new AssignmentRuleItem(m.Name, m.Module, registry.SourceOf(m.Name)!.Value, row is not null, set?.Rules.Count ?? 0, set?.Rules.Count(r => r.Active) ?? 0);
            }).ToList();
    }

    private static AssignmentRuleSet? TryParse(string json)
    {
        try
        {
            return DocTypeLoader.ParseAssignmentRules(json);
        }
        catch (DocTypeValidationException)
        {
            return null;
        }
    }

    public async Task<AssignmentRuleView> GetAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = Workable(doctype, out var source);
        var stored = await store.GetAsync(doctype, ct);
        var set = (stored is null ? null : TryParse(stored.Definition)) ?? new AssignmentRuleSet { DocType = doctype };
        return new AssignmentRuleView(doctype, source, stored is not null, stored?.Modified, set, UserFieldsOf(meta), CanManage(user));
    }

    public async Task<AssignmentRuleView> SaveAsync(
        string doctype, AssignmentRuleSet set, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
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
                throw new ConflictException($"The assignment rules of '{doctype}' were changed by someone else. Reload and try again.");

            var utc = clock.GetUtcNow().UtcDateTime;
            var row = new StoredAssignmentRules(doctype, DocTypeLoader.SerializeAssignmentRules(set), new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc), user.User);
            if (!await store.SaveAsync(row, expectedModified, ct))
                throw new ConflictException($"The assignment rules of '{doctype}' were changed by someone else. Reload and try again.");

            catalog.Set(set);
            return await GetAsync(doctype, user, ct);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Deletes the rules. Assignments already made stay.</summary>
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

    /// <summary>Loads every stored rule set into the catalog (at start-up). A broken one is skipped and reported.</summary>
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                var set = DocTypeLoader.ParseAssignmentRules(row.Definition);
                var meta = Workable(row.DocType, out _);
                // Users may have been disabled since; only the shape is checked here, so a rule is never lost to a missing user.
                var errors = ValidateShape(Clean(set), meta);
                if (errors.Count > 0)
                    throw new DocTypeValidationException(string.Join(" ", errors.Select(e => $"{e.Field}: {e.Message}")));
                catalog.Set(set);
            }
            catch (Exception ex) when (ex is DocTypeValidationException or NotFoundException)
            {
                failures.Add($"The assignment rules of '{row.DocType}' were skipped: {ex.Message}");
            }
        }

        return failures;
    }

    private static AssignmentRuleSet Clean(AssignmentRuleSet input) => input with
    {
        Rules = (input.Rules ?? []).Select(r => r with
        {
            Name = r.Name?.Trim() ?? string.Empty,
            Strategy = r.Strategy?.Trim() ?? string.Empty,
            Users = (r.Users ?? []).Select(u => u?.Trim() ?? string.Empty).Where(u => u.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            UserField = string.IsNullOrWhiteSpace(r.UserField) ? null : r.UserField.Trim(),
            Condition = string.IsNullOrWhiteSpace(r.Condition) ? null : r.Condition.Trim(),
            CloseCondition = string.IsNullOrWhiteSpace(r.CloseCondition) ? null : r.CloseCondition.Trim(),
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
            Priority = string.IsNullOrWhiteSpace(r.Priority) ? "Medium" : r.Priority.Trim(),
        }).ToList(),
    };

    private async Task<List<FieldError>> ValidateAsync(AssignmentRuleSet set, DocTypeMeta meta, CancellationToken ct)
    {
        var errors = ValidateShape(set, meta);
        if (!registry.TryGet("User", out var userMeta))
            return errors;
        for (var i = 0; i < set.Rules.Count; i++)
        {
            foreach (var user in set.Rules[i].Users.Take(AssignmentRule.MaxUsers))
            {
                if (user.Length <= FieldTypes.ShortTextLength && !await repository.ExistsAsync(userMeta, user, ct))
                    errors.Add(new FieldError($"rules[{i}].users", $"'{user}' is not a user."));
            }
        }

        return errors;
    }

    /// <summary>Every problem with a rule set that can be found without looking at users, keyed by path (<c>rules[0].condition</c>).</summary>
    public static List<FieldError> ValidateShape(AssignmentRuleSet set, DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        void Add(string path, string message) => errors.Add(new FieldError(path, message));

        if (set.Rules.Count > AssignmentRule.MaxRules)
            Add("rules", $"At most {AssignmentRule.MaxRules} rules.");

        var known = meta.Fields.Select(f => f.Name).Concat(StandardFields.Columns.Keys).ToHashSet(StringComparer.Ordinal);
        var userFields = UserFieldsOf(meta);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CheckExpression(string path, string? text)
        {
            if (text is null)
                return;
            if (text.Length > AssignmentRule.MaxCondition)
            {
                Add(path, $"At most {AssignmentRule.MaxCondition} characters.");
                return;
            }

            try
            {
                var unknown = Expression.Parse(text).Identifiers.FirstOrDefault(n => !known.Contains(n));
                if (unknown is not null)
                    Add(path, $"'{unknown}' is not a field of this DocType.");
            }
            catch (ExpressionException ex)
            {
                Add(path, ex.Message);
            }
        }

        for (var i = 0; i < set.Rules.Count; i++)
        {
            var r = set.Rules[i];
            if (r.Name.Length is 0 or > 100 || r.Name.Any(char.IsControl))
                Add($"rules[{i}].name", "Give the rule a name of up to 100 characters.");
            else if (!names.Add(r.Name))
                Add($"rules[{i}].name", $"There is already a rule named '{r.Name}'.");

            if (!AssignmentRule.Strategies.Contains(r.Strategy))
                Add($"rules[{i}].strategy", $"Use one of: {string.Join(", ", AssignmentRule.Strategies)}.");
            else if (r.Strategy == AssignmentRule.Field)
            {
                if (r.UserField is null || !userFields.Contains(r.UserField))
                    Add($"rules[{i}].userField", "Choose the field that holds the user (a Link to User, or a Data or Email field).");
            }
            else if (r.Users.Count == 0)
                Add($"rules[{i}].users", "Choose at least one person.");
            else if (r.Users.Count > AssignmentRule.MaxUsers)
                Add($"rules[{i}].users", $"At most {AssignmentRule.MaxUsers} people.");

            if (r.Users.Any(u => u.Length > FieldTypes.ShortTextLength))
                Add($"rules[{i}].users", "A user name is too long.");
            if (!AssignmentRule.Priorities.Contains(r.Priority))
                Add($"rules[{i}].priority", "Use Low, Medium or High.");
            if (r.DueInDays is < 0 or > 365)
                Add($"rules[{i}].dueInDays", "Use a number of days from 0 to 365.");
            if (r.Description is { } text && (text.Length > AssignmentRule.MaxDescription || text.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
                Add($"rules[{i}].description", $"Keep the description to {AssignmentRule.MaxDescription} characters of plain text.");

            CheckExpression($"rules[{i}].condition", r.Condition);
            CheckExpression($"rules[{i}].closeCondition", r.CloseCondition);
        }

        return errors;
    }
}

/// <summary>
/// Applies assignment rules after a document is saved: the first active rule whose condition holds gives the document to
/// someone (once per document), and a rule can withdraw its assignment when its close condition holds. Picking is
/// stateless: round robin takes whoever was given something by the rule longest ago, load balancing whoever has the fewest
/// open assignments, both worked out from the assignments themselves.
/// </summary>
public sealed class AssignmentRuleEngine(
    AssignmentRuleCatalog catalog,
    IMetaRegistry registry,
    IDocumentRepository repository,
    AssignmentService assignments,
    NotificationService notifications)
{
    private const int HistoryLimit = 1000;
    private const int OpenLimit = 10000;

    public async Task AfterSaveAsync(
        DocTypeMeta meta,
        string docName,
        IReadOnlyDictionary<string, object?> doc,
        Func<string?, bool> holds,
        IUserContext actor,
        CancellationToken ct = default)
    {
        if (!AssignmentRuleManager.Applies(meta) || catalog.For(meta.Name) is not { } set)
            return;

        try
        {
            await ApplyAsync(meta, docName, doc, set, holds, actor, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The document is already saved; a rule that cannot run (a user disabled meanwhile, a database hiccup) must not turn
            // that into an error for the person who saved it. The next save tries again.
        }
    }

    private async Task ApplyAsync(
        DocTypeMeta meta, string docName, IReadOnlyDictionary<string, object?> doc, AssignmentRuleSet set, Func<string?, bool> holds, IUserContext actor, CancellationToken ct)
    {
        var made = (await assignments.RuleAssignmentsAsync(meta.Name, docName, ct)).ToList();

        foreach (var row in made.Where(r => (r["status"] as string) == "Open"))
        {
            var rule = set.Rules.FirstOrDefault(r => r.Active && string.Equals(r.Name, row["assignment_rule"] as string, StringComparison.Ordinal));
            if (rule?.CloseCondition is not null && holds(rule.CloseCondition))
                await assignments.WithdrawForRuleAsync((string)row["name"]!, rule.Name, actor, ct);
        }

        if (made.Count > 0)
            return; // one rule gives a document away once

        foreach (var rule in set.Rules.Where(r => r.Active))
        {
            if (!holds(rule.Condition))
                continue;
            if (await PickAsync(meta, rule, doc, ct) is not { } person)
                continue;

            await assignments.AssignByRuleAsync(meta.Name, docName, person, rule, actor, ct);
            return;
        }
    }

    private async Task<UserContext?> PickAsync(DocTypeMeta meta, AssignmentRule rule, IReadOnlyDictionary<string, object?> doc, CancellationToken ct)
    {
        if (rule.Strategy == AssignmentRule.Field)
            return doc.GetValueOrDefault(rule.UserField ?? string.Empty) is string who && who.Length > 0 ? await notifications.ContextOfAsync(who, ct) : null;

        var candidates = new List<UserContext>();
        foreach (var email in rule.Users)
        {
            if (await notifications.ContextOfAsync(email, ct) is { } person)
                candidates.Add(person);
        }

        if (candidates.Count == 0 || !registry.TryGet(AssignmentService.DocTypeName, out var todo))
            return null;

        if (rule.Strategy == AssignmentRule.LoadBalancing)
        {
            var open = await repository.ListAsync(todo, new ListQuery
            {
                Filters = [new("status", FilterOp.Eq, "Open"), new("allocated_to", FilterOp.In, candidates.Select(c => (object?)c.User).ToList())],
                Fields = ["allocated_to"], OrderBy = "creation", Descending = false, Limit = OpenLimit,
            }, ct);
            var counts = open.GroupBy(r => (string)r["allocated_to"]!).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            return candidates.MinBy(c => counts.GetValueOrDefault(c.User));
        }

        // Round robin: whoever the rule gave a document of this DocType to longest ago (never counts as longest ago).
        var given = await repository.ListAsync(todo, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, meta.Name), new("assignment_rule", FilterOp.Eq, rule.Name)],
            Fields = ["allocated_to", "creation"], OrderBy = "creation", Descending = true, Limit = HistoryLimit,
        }, ct);
        var last = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        foreach (var row in given)
            last.TryAdd((string)row["allocated_to"]!, (DateTime)row["creation"]!); // newest first, so the first one seen is the latest
        return candidates.MinBy(c => last.GetValueOrDefault(c.User, DateTime.MinValue));
    }
}
