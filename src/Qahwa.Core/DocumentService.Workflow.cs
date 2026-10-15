using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>Workflow: the state of a document, who may move it on, and the inbox of documents waiting for a person.</summary>
public sealed partial class DocumentService
{
    private WorkflowDefinition? WorkflowOf(DocTypeMeta meta) => workflows?.For(meta.Name);

    /// <summary>The state a document is in; a document with no (or an unknown) state is in the first state.</summary>
    private static WorkflowState StateOf(WorkflowDefinition workflow, IReadOnlyDictionary<string, object?> doc) =>
        workflow.States.FirstOrDefault(s => doc.GetValueOrDefault(workflow.StateField) is string text && string.Equals(s.Name, text, StringComparison.Ordinal))
        ?? workflow.Initial;

    /// <summary>Administrators can do any step; everyone else needs one of the step's roles.</summary>
    private bool HoldsRole(IUserContext user, IEnumerable<string> roles) =>
        permissions.IsAdministrator(user) || roles.Any(r => user.Roles.Contains(r, StringComparer.Ordinal));

    private bool ConditionHolds(string? condition, DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, IUserContext user)
    {
        if (condition is null)
            return true;
        try
        {
            return Expression.Parse(condition).IsTrue(_logic.Context(meta, doc, [], null, user, isNew: false));
        }
        catch (ExpressionException)
        {
            return false; // a condition that cannot be evaluated offers nothing
        }
    }

    private IReadOnlyList<WorkflowAction> ActionsFor(WorkflowDefinition workflow, DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, IUserContext user)
    {
        var state = StateOf(workflow, doc).Name;
        var owner = doc.GetValueOrDefault("owner") as string;
        return workflow.Transitions
            .Where(t => t.From == state && HoldsRole(user, t.Roles))
            .Where(t => t.AllowSelfApproval || !string.Equals(owner, user.User, StringComparison.Ordinal))
            .Where(t => ConditionHolds(t.Condition, meta, doc, user))
            .Select(t => new WorkflowAction(t.Action, t.To))
            .ToList();
    }

    /// <summary>The state of a document and what this person can do with it, or null when the DocType has no active workflow.</summary>
    public async Task<WorkflowStatus?> WorkflowStatusAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        if (WorkflowOf(meta) is not { } workflow)
            return null;

        await GetAsync(doctype, name, user, ct); // proves the person may read it
        var raw = await LoadAsync(meta, name, ct) ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        var state = StateOf(workflow, raw);
        return new WorkflowStatus(workflow.StateField, state.Name, state.Color, ActionsFor(workflow, meta, raw, user),
            state.EditRoles.Count == 0 || HoldsRole(user, state.EditRoles));
    }

    /// <summary>
    /// Does one workflow action on a document: the state changes (and the document is submitted or cancelled if the new state
    /// says so) through the ordinary update, so validation, locking, the updates log and notifications all apply.
    /// The person needs to read the document and to hold a role of the step; they do not need write access.
    /// </summary>
    public async Task<Dictionary<string, object?>> ApplyWorkflowActionAsync(
        string doctype, string name, string? action, DateTime? modified, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var workflow = WorkflowOf(meta) ?? throw General($"{meta.Name} has no active workflow.");

        await GetAsync(doctype, name, user, ct);
        var raw = await LoadAsync(meta, name, ct) ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        var available = ActionsFor(workflow, meta, raw, user);
        var chosen = available.FirstOrDefault(a => string.Equals(a.Action, action?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new PermissionDeniedException($"'{action}' is not available to you for this {meta.Name} in the state '{StateOf(workflow, raw).Name}'.");

        var target = workflow.States.First(s => s.Name == chosen.To);
        var current = Convert.ToInt32(raw.GetValueOrDefault("docstatus") ?? 0);
        var step = new WorkflowStep(workflow.StateField, target.Name, target.DocStatus != current ? target.DocStatus : null);
        return await UpdateCoreAsync(doctype, name, TransitionInput(modified), user, transition: null, step, ct);
    }

    /// <summary>Documents waiting for this person's action, across every workflow (the inbox on the home page).</summary>
    public async Task<IReadOnlyList<PendingAction>> PendingWorkflowAsync(IUserContext user, CancellationToken ct = default)
    {
        const int PerDocType = 25;
        const int Total = 100;
        var result = new List<PendingAction>();
        if (workflows is null || !user.IsAuthenticated)
            return result;

        foreach (var workflow in workflows.All.OrderBy(w => w.DocType, StringComparer.Ordinal))
        {
            if (!registry.TryGet(workflow.DocType, out var meta))
                continue;
            var states = workflow.Transitions.Where(t => HoldsRole(user, t.Roles)).Select(t => t.From).Distinct(StringComparer.Ordinal).ToList();
            if (states.Count == 0)
                continue;

            try
            {
                var title = meta.TitleField is { } t && t != "name" ? t : null;
                var fields = new List<string> { "name", workflow.StateField, "modified" };
                if (title is not null)
                    fields.Add(title);
                var rows = (await ListAsync(workflow.DocType, new ListRequest
                {
                    Filters = [new RawFilter(workflow.StateField, "in", states)], Fields = fields, OrderBy = "modified", Descending = true, Limit = PerDocType,
                }, user, ct)).Data;

                foreach (var row in rows)
                {
                    var raw = await LoadAsync(meta, (string)row["name"]!, ct);
                    if (raw is null)
                        continue;
                    var actions = ActionsFor(workflow, meta, raw, user);
                    if (actions.Count > 0)
                    {
                        var state = StateOf(workflow, raw);
                        result.Add(new PendingAction(workflow.DocType, (string)row["name"]!, title is null ? null : row.GetValueOrDefault(title) as string,
                            state.Name, state.Color, (DateTime)row["modified"]!, actions));
                    }
                }
            }
            catch (Exception ex) when (ex is PermissionDeniedException or NotFoundException or DocumentValidationException)
            {
                // a DocType the person cannot open (or whose state field they cannot read) has nothing for them here
            }
        }

        return result.OrderByDescending(p => p.Modified).Take(Total).ToList();
    }
}
