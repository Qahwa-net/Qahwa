using System.Collections.Immutable;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>One state a document can be in (Draft, Pending Approval, Approved ...).</summary>
public sealed record WorkflowState
{
    public required string Name { get; init; }

    /// <summary>gray, blue, green, orange, red or purple.</summary>
    public string Color { get; init; } = "gray";

    /// <summary>For submittable DocTypes: 0 draft, 1 submitted, 2 cancelled. Moving into a state with a higher value submits or cancels the document.</summary>
    public int DocStatus { get; init; }

    /// <summary>Roles that may edit the document while it is in this state; empty means anyone with write access.</summary>
    public IReadOnlyList<string> EditRoles { get; init; } = [];

    /// <summary>
    /// Escalation: when a document has been in this state, untouched, for this many hours (1 to 8760; 0 is off), the
    /// scheduler tells <see cref="EscalateNotify"/> and/or does <see cref="EscalateAction"/> for it. Once per time in the state.
    /// </summary>
    public int EscalateAfterHours { get; init; }

    /// <summary>The action (a transition leaving this state, needing only one person) the system does when time is up.</summary>
    public string? EscalateAction { get; init; }

    /// <summary>People holding these roles are told when time is up.</summary>
    public IReadOnlyList<string> EscalateNotify { get; init; } = [];

    /// <summary>
    /// When a document moves into this state, email each person who can act on it a message with one link per action.
    /// The link opens a page that asks them to sign in and confirm; nothing happens from a click alone.
    /// </summary>
    public bool EmailApprovers { get; init; }
}

/// <summary>A move from one state to another, done with a named action by people with one of the roles.</summary>
public sealed record WorkflowTransition
{
    public required string From { get; init; }
    public required string Action { get; init; }
    public required string To { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>An expression over the document's fields; the action is only offered while it is true.</summary>
    public string? Condition { get; init; }

    /// <summary>When false, the person who created the document cannot do this step themselves.</summary>
    public bool AllowSelfApproval { get; init; } = true;

    /// <summary>
    /// How many different people must do this action before the document moves (1 to 10). With more than one, each person's
    /// action is recorded and the document stays where it is until the last one is in.
    /// </summary>
    public int RequiredApprovals { get; init; } = 1;
}

/// <summary>The approval process of one DocType. The first state is where new documents start.</summary>
public sealed record WorkflowDefinition
{
    public const int MaxStates = 30;
    public const int MaxTransitions = 100;
    public const int MaxCondition = 500;
    public const int MaxApprovals = 10;
    public const int MaxEscalateHours = 8760;
    public const int MaxEscalateRoles = 10;

    public required string DocType { get; init; }

    /// <summary>The field that holds the current state. Only the workflow changes it.</summary>
    public required string StateField { get; init; }

    public bool Active { get; init; } = true;
    public IReadOnlyList<WorkflowState> States { get; init; } = [];
    public IReadOnlyList<WorkflowTransition> Transitions { get; init; } = [];

    [JsonIgnore]
    public WorkflowState Initial => States[0];
}

/// <summary>What one person can do with one document in its workflow.</summary>
/// <param name="Required">How many people must do this action; <paramref name="Given"/> of them already have.</param>
public sealed record WorkflowAction(string Action, string To, int Required = 1, int Given = 0);

public sealed record WorkflowStatus(string StateField, string State, string Color, IReadOnlyList<WorkflowAction> Actions, bool CanEdit);

/// <summary>A document waiting for the person's action.</summary>
public sealed record PendingAction(string DocType, string Name, string? Title, string State, string Color, DateTime Modified, IReadOnlyList<WorkflowAction> Actions);

/// <summary>The step a workflow action takes on a document: set the state, and move the document status if the state asks for it.</summary>
internal sealed record WorkflowStep(string Field, string State, int? DocStatus);

public sealed record StoredWorkflow(string DocType, string Definition, DateTime Modified, string ModifiedBy);

public interface IWorkflowStore
{
    Task<IReadOnlyList<StoredWorkflow>> ListAsync(CancellationToken ct = default);

    Task<StoredWorkflow?> GetAsync(string docType, CancellationToken ct = default);

    /// <param name="expectedModified">Null when the DocType has no workflow yet.</param>
    Task<bool> SaveAsync(StoredWorkflow row, DateTime? expectedModified, CancellationToken ct = default);

    Task<bool> DeleteAsync(string docType, CancellationToken ct = default);
}

/// <summary>The active workflows, as an immutable snapshot the document engine reads on every write.</summary>
public sealed class WorkflowCatalog
{
    private ImmutableDictionary<string, WorkflowDefinition> _active = ImmutableDictionary.Create<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<WorkflowDefinition> All => _active.Values.ToList();

    public WorkflowDefinition? For(string doctype) => _active.GetValueOrDefault(doctype);

    internal void Set(WorkflowDefinition definition)
    {
        if (definition.Active)
            ImmutableInterlocked.AddOrUpdate(ref _active, definition.DocType, definition, (_, _) => definition);
        else
            ImmutableInterlocked.TryRemove(ref _active, definition.DocType, out _);
    }

    internal void Remove(string doctype) => ImmutableInterlocked.TryRemove(ref _active, doctype, out _);
}

public sealed record WorkflowItem(string DocType, string? Module, DocTypeSource Source, bool HasWorkflow, bool Active, int States);

public sealed record WorkflowView(
    string DocType,
    DocTypeSource Source,
    bool Exists,
    DateTime? Modified,
    WorkflowDefinition Workflow,
    IReadOnlyList<string> StateFields,
    bool CanEdit);

/// <summary>
/// The Workflow Builder: define who moves a document from which state to which, with what condition. A workflow is data:
/// it is validated against the DocType, stored, and takes effect at once. Managing workflows needs a System Manager;
/// the framework's own DocTypes need an Administrator.
/// </summary>
public sealed partial class WorkflowManager(
    IMetaRegistry registry,
    IWorkflowStore store,
    WorkflowCatalog catalog,
    PermissionService permissions,
    TimeProvider clock)
{
    private readonly SemaphoreSlim _changes = new(1, 1);

    [GeneratedRegex("^[A-Za-z0-9 _\\-/().]{1,140}$")]
    private static partial Regex NameRegex();

    private bool CanManage(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!CanManage(user))
            throw new PermissionDeniedException("Only a System Manager can manage workflows.");
    }

    private bool CanEdit(DocTypeSource source, IUserContext user) =>
        source == DocTypeSource.Core ? permissions.IsAdministrator(user) : CanManage(user);

    private DocTypeMeta Workable(string doctype, out DocTypeSource source)
    {
        if (!registry.TryGet(doctype, out var meta) || meta.IsChildTable || meta.IsSingle)
            throw new NotFoundException($"DocType '{doctype}' was not found.");
        source = registry.SourceOf(doctype)!.Value;
        return meta;
    }

    /// <summary>The fields that can hold the state: Select, Data or Link fields (not passwords, not calculated).</summary>
    public static IReadOnlyList<string> StateFieldsOf(DocTypeMeta meta) =>
        meta.Fields.Where(f => f.Type is FieldType.Select or FieldType.Data or FieldType.Link).Where(f => f.Formula is null).Select(f => f.Name).ToList();

    public async Task<IReadOnlyList<WorkflowItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var stored = (await store.ListAsync(ct)).ToDictionary(r => r.DocType, StringComparer.OrdinalIgnoreCase);
        return registry.All.Where(m => !m.IsChildTable && !m.IsSingle).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var definition = stored.TryGetValue(m.Name, out var row) ? TryParse(row.Definition) : null;
                return new WorkflowItem(m.Name, m.Module, registry.SourceOf(m.Name)!.Value, row is not null, definition?.Active ?? false, definition?.States.Count ?? 0);
            }).ToList();
    }

    private static WorkflowDefinition? TryParse(string json)
    {
        try
        {
            return DocTypeLoader.ParseWorkflow(json);
        }
        catch (DocTypeValidationException)
        {
            return null;
        }
    }

    public async Task<WorkflowView> GetAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var meta = Workable(doctype, out var source);
        var stored = await store.GetAsync(doctype, ct);
        var definition = (stored is null ? null : TryParse(stored.Definition))
            ?? new WorkflowDefinition { DocType = doctype, StateField = StateFieldsOf(meta).FirstOrDefault(f => f.Contains("state", StringComparison.OrdinalIgnoreCase)) ?? string.Empty, Active = false };
        return new WorkflowView(doctype, source, stored is not null, stored?.Modified, definition, StateFieldsOf(meta), CanEdit(source, user));
    }

    public async Task<WorkflowView> SaveAsync(
        string doctype, WorkflowDefinition definition, DateTime? expectedModified, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            var meta = Workable(doctype, out var source);
            if (!CanEdit(source, user))
                throw new PermissionDeniedException("Only an Administrator can add a workflow to the framework's own DocTypes.");

            definition = Clean(definition with { DocType = doctype });
            var errors = Validate(definition, meta);
            if (errors.Count > 0)
                throw new DocumentValidationException(errors);

            var stored = await store.GetAsync(doctype, ct);
            if (stored?.Modified != expectedModified)
                throw new ConflictException($"The workflow of '{doctype}' was changed by someone else. Reload and try again.");

            var utc = clock.GetUtcNow().UtcDateTime;
            var row = new StoredWorkflow(doctype, DocTypeLoader.SerializeWorkflow(definition), new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc), user.User);
            if (!await store.SaveAsync(row, expectedModified, ct))
                throw new ConflictException($"The workflow of '{doctype}' was changed by someone else. Reload and try again.");

            catalog.Set(definition);
            return await GetAsync(doctype, user, ct);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Deletes the workflow. Documents keep their state text; they are simply no longer controlled.</summary>
    public async Task DeleteAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        await _changes.WaitAsync(ct);
        try
        {
            Workable(doctype, out var source);
            if (!CanEdit(source, user))
                throw new PermissionDeniedException("Only an Administrator can change the framework's own DocTypes.");
            await store.DeleteAsync(doctype, ct);
            catalog.Remove(doctype);
        }
        finally
        {
            _changes.Release();
        }
    }

    /// <summary>Loads every stored workflow into the catalog (at start-up). A broken one is skipped and reported.</summary>
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                var definition = DocTypeLoader.ParseWorkflow(row.Definition);
                var meta = Workable(row.DocType, out _);
                var errors = Validate(Clean(definition), meta);
                if (errors.Count > 0)
                    throw new DocTypeValidationException(string.Join(" ", errors.Select(e => $"{e.Field}: {e.Message}")));
                catalog.Set(definition);
            }
            catch (Exception ex) when (ex is DocTypeValidationException or NotFoundException)
            {
                failures.Add($"The workflow of '{row.DocType}' was skipped: {ex.Message}");
            }
        }

        return failures;
    }

    private static WorkflowDefinition Clean(WorkflowDefinition input)
    {
        var d = CleanText(input);
        // A transition may name a state in any case; keep the state's own spelling so the engine can compare exactly.
        string Exact(string name) => d.States.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))?.Name ?? name;
        return d with { Transitions = d.Transitions.Select(t => t with { From = Exact(t.From), To = Exact(t.To) }).ToList() };
    }

    private static WorkflowDefinition CleanText(WorkflowDefinition d) => d with
    {
        StateField = d.StateField?.Trim() ?? string.Empty,
        States = (d.States ?? []).Select(s => s with { Name = s.Name?.Trim() ?? string.Empty, Color = string.IsNullOrWhiteSpace(s.Color) ? "gray" : s.Color.Trim().ToLowerInvariant(), EditRoles = Roles(s.EditRoles), EscalateAction = string.IsNullOrWhiteSpace(s.EscalateAction) ? null : s.EscalateAction.Trim(), EscalateNotify = Roles(s.EscalateNotify) }).ToList(),
        Transitions = (d.Transitions ?? []).Select(t => t with
        {
            From = t.From?.Trim() ?? string.Empty, To = t.To?.Trim() ?? string.Empty, Action = t.Action?.Trim() ?? string.Empty,
            Roles = Roles(t.Roles), Condition = string.IsNullOrWhiteSpace(t.Condition) ? null : t.Condition.Trim(),
        }).ToList(),
    };

    private static IReadOnlyList<string> Roles(IReadOnlyList<string>? roles) =>
        (roles ?? []).Select(r => r?.Trim() ?? string.Empty).Where(r => r.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Every problem with a workflow, keyed by path (<c>states[1].name</c>, <c>transitions[0].roles</c>).</summary>
    public static List<FieldError> Validate(WorkflowDefinition d, DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        void Add(string path, string message) => errors.Add(new FieldError(path, message));

        var field = meta.Fields.FirstOrDefault(f => f.Name == d.StateField);
        if (field is null || !StateFieldsOf(meta).Contains(field.Name))
            Add("stateField", "Choose the Select, Data or Link field that will hold the state.");

        if (d.States.Count == 0)
            Add("states", "A workflow needs at least one state; the first is where new documents start.");
        if (d.States.Count > WorkflowDefinition.MaxStates)
            Add("states", $"At most {WorkflowDefinition.MaxStates} states.");
        if (d.Transitions.Count > WorkflowDefinition.MaxTransitions)
            Add("transitions", $"At most {WorkflowDefinition.MaxTransitions} transitions.");

        var names = new Dictionary<string, WorkflowState>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < d.States.Count; i++)
        {
            var state = d.States[i];
            if (!NameRegex().IsMatch(state.Name))
                Add($"states[{i}].name", "Give the state a name of up to 140 letters, digits, spaces and - _ / ( ) .");
            else if (!names.TryAdd(state.Name, state))
                Add($"states[{i}].name", $"There is already a state named '{state.Name}'.");
            if (!IndicatorMeta.Colors.Contains(state.Color))
                Add($"states[{i}].color", $"Use one of: {string.Join(", ", IndicatorMeta.Colors.Order())}.");
            if (state.DocStatus is < 0 or > 2 || (!meta.IsSubmittable && state.DocStatus != 0))
                Add($"states[{i}].docStatus", meta.IsSubmittable ? "Use 0 (draft), 1 (submitted) or 2 (cancelled)." : "Only a submittable DocType can submit or cancel documents; use 0.");
            if (i == 0 && state.DocStatus != 0)
                Add($"states[{i}].docStatus", "New documents start as drafts, so the first state must be 0.");
            if (state.EditRoles.Any(r => r.Length > FieldTypes.ShortTextLength))
                Add($"states[{i}].editRoles", "A role name is too long.");
            if (state.EscalateAfterHours is < 0 or > WorkflowDefinition.MaxEscalateHours)
                Add($"states[{i}].escalateAfterHours", $"Use 0 (off) or a number of hours up to {WorkflowDefinition.MaxEscalateHours}.");
            else if (state.EscalateAfterHours > 0)
            {
                if (state.EscalateAction is null && state.EscalateNotify.Count == 0)
                    Add($"states[{i}].escalateAction", "Say what happens when time is up: choose an action, or roles to tell.");
                if (state.EscalateNotify.Count > WorkflowDefinition.MaxEscalateRoles || state.EscalateNotify.Any(r => r.Length > FieldTypes.ShortTextLength))
                    Add($"states[{i}].escalateNotify", $"At most {WorkflowDefinition.MaxEscalateRoles} roles.");
                if (state.EscalateAction is { } escalate)
                {
                    var step = d.Transitions.FirstOrDefault(t => string.Equals(t.From, state.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(t.Action, escalate, StringComparison.OrdinalIgnoreCase));
                    if (step is null)
                        Add($"states[{i}].escalateAction", $"'{escalate}' is not an action that leaves '{state.Name}'.");
                    else if (step.RequiredApprovals > 1)
                        Add($"states[{i}].escalateAction", "An action that needs several people cannot be done by the system.");
                }
            }
            if (field?.Type == FieldType.Select && NameRegex().IsMatch(state.Name)
                && !(field.Options ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries).Contains(state.Name, StringComparer.Ordinal))
            {
                Add($"states[{i}].name", $"'{state.Name}' is not one of the choices of {field.Name}. Add it there, or use a Data field for the state.");
            }
        }

        var known = meta.Fields.Select(f => f.Name).Concat(StandardFields.Columns.Keys).ToHashSet(StringComparer.Ordinal);
        var pairs = new HashSet<(string, string)>();
        for (var i = 0; i < d.Transitions.Count; i++)
        {
            var t = d.Transitions[i];
            var from = names.GetValueOrDefault(t.From);
            var to = names.GetValueOrDefault(t.To);
            if (from is null)
                Add($"transitions[{i}].from", $"'{t.From}' is not a state of this workflow.");
            if (to is null)
                Add($"transitions[{i}].to", $"'{t.To}' is not a state of this workflow.");
            if (t.Action.Length is 0 or > 60 || t.Action.Any(char.IsControl))
                Add($"transitions[{i}].action", "Name the action (up to 60 characters), for example Approve.");
            else if (!pairs.Add((t.From.ToLowerInvariant(), t.Action.ToLowerInvariant())))
                Add($"transitions[{i}].action", $"'{t.Action}' already leaves the state '{t.From}'.");
            if (t.Roles.Count == 0 || t.Roles.Any(r => r.Length > FieldTypes.ShortTextLength))
                Add($"transitions[{i}].roles", "Say which roles may do this (at least one).");
            if (from is not null && to is not null && !ValidMove(from.DocStatus, to.DocStatus))
                Add($"transitions[{i}].to", "A document can be submitted (0 to 1) or cancelled (1 to 2), never moved back, and a cancelled one stays cancelled.");
            if (t.RequiredApprovals is < 1 or > WorkflowDefinition.MaxApprovals)
                Add($"transitions[{i}].requiredApprovals", $"Use a number of people from 1 to {WorkflowDefinition.MaxApprovals}.");
            if (t.Condition is { } condition)
            {
                if (condition.Length > WorkflowDefinition.MaxCondition)
                    Add($"transitions[{i}].condition", $"At most {WorkflowDefinition.MaxCondition} characters.");
                else
                {
                    try
                    {
                        var unknown = Expression.Parse(condition).Identifiers.FirstOrDefault(n => !known.Contains(n));
                        if (unknown is not null)
                            Add($"transitions[{i}].condition", $"'{unknown}' is not a field of this DocType.");
                    }
                    catch (ExpressionException ex)
                    {
                        Add($"transitions[{i}].condition", ex.Message);
                    }
                }
            }
        }

        return errors;
    }

    private static bool ValidMove(int from, int to) => to == from || (from == 0 && to == 1) || (from == 1 && to == 2);
}
