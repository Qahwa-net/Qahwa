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

    /// <summary>Whether a condition (an expression over the document's fields) holds; an empty one always does and a broken one never.</summary>
    public bool EvaluateCondition(string? condition, DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, IUserContext user) =>
        ConditionHolds(condition, meta, doc, user);

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

    private const string ApprovalDocType = "Workflow Approval";

    /// <summary>Who has already done which action on a document while it is in a state (only for steps that need several people).</summary>
    private async Task<IReadOnlyList<Dictionary<string, object?>>> VotesAsync(string doctype, string name, string? state, CancellationToken ct)
    {
        if (!registry.TryGet(ApprovalDocType, out var meta))
            return [];
        List<QueryFilter> filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)];
        if (state is not null)
            filters.Add(new("state", FilterOp.Eq, state));
        return await repository.ListAsync(meta, new ListQuery { Filters = filters, Fields = ["name", "action", "user"], OrderBy = "creation", Descending = false, Limit = 500 }, ct);
    }

    private async Task<IReadOnlyList<WorkflowAction>> ActionsForAsync(
        WorkflowDefinition workflow, DocTypeMeta meta, IReadOnlyDictionary<string, object?> doc, IUserContext user, CancellationToken ct)
    {
        var state = StateOf(workflow, doc).Name;
        var owner = doc.GetValueOrDefault("owner") as string;
        var steps = workflow.Transitions
            .Where(t => t.From == state && HoldsRole(user, t.Roles))
            .Where(t => t.AllowSelfApproval || !string.Equals(owner, user.User, StringComparison.Ordinal))
            .Where(t => ConditionHolds(t.Condition, meta, doc, user))
            .ToList();

        var votes = steps.Any(t => t.RequiredApprovals > 1) ? await VotesAsync(meta.Name, (string)doc["name"]!, state, ct) : [];
        var actions = new List<WorkflowAction>();
        foreach (var t in steps)
        {
            var given = votes.Where(v => string.Equals(v["action"] as string, t.Action, StringComparison.Ordinal)).Select(v => v["user"] as string).ToList();
            if (t.RequiredApprovals > 1 && given.Contains(user.User, StringComparer.Ordinal))
                continue; // this person's say is already in
            actions.Add(new WorkflowAction(t.Action, t.To, t.RequiredApprovals, t.RequiredApprovals > 1 ? given.Count : 0));
        }
        return actions;
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
        return new WorkflowStatus(workflow.StateField, state.Name, state.Color, await ActionsForAsync(workflow, meta, raw, user, ct),
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
        var available = await ActionsForAsync(workflow, meta, raw, user, ct);
        var chosen = available.FirstOrDefault(a => string.Equals(a.Action, action?.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new PermissionDeniedException($"'{action}' is not available to you for this {meta.Name} in the state '{StateOf(workflow, raw).Name}'.");

        // A step that needs several people records this person's say and waits for the rest.
        if (chosen.Required > 1 && chosen.Given + 1 < chosen.Required)
        {
            await RecordApprovalAsync(doctype, name, StateOf(workflow, raw).Name, chosen, user, ct);
            return await GetAsync(doctype, name, user, ct);
        }

        var target = workflow.States.First(s => s.Name == chosen.To);
        var current = Convert.ToInt32(raw.GetValueOrDefault("docstatus") ?? 0);
        var step = new WorkflowStep(workflow.StateField, target.Name, target.DocStatus != current ? target.DocStatus : null);
        var result = await UpdateCoreAsync(doctype, name, TransitionInput(modified), user, transition: null, step, ct);
        if (workflow.Transitions.Any(t => t.RequiredApprovals > 1) || workflow.States.Any(st => st.EscalateAfterHours > 0 || st.EmailApprovers))
            await ClearApprovalsAsync(doctype, name, ct); // the document moved on: the next state starts with nobody having approved, no escalation done and no old links
        if (workflowLinks is not null && target.EmailApprovers)
        {
            try
            {
                await workflowLinks.Value.SendAsync(doctype, name, ct);
            }
            catch (Exception ex) when (ex is DocumentValidationException or PermissionDeniedException or NotFoundException or ConflictException)
            {
                // the move is done; a problem writing the emails must not undo it
            }
        }

        return result;
    }

    private async Task RecordApprovalAsync(string doctype, string name, string state, WorkflowAction action, IUserContext user, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        now = new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc);
        var row = HistoryRecorder.Standard(user.User, now);
        row["reference_doctype"] = doctype;
        row["reference_name"] = name;
        row["state"] = state;
        row["action"] = action.Action;
        row["user"] = user.User;
        await repository.InsertAsync(registry.Get(ApprovalDocType), row, null, ct);

        if (history is not null && registry.TryGet(HistoryRecorder.CommentDocType, out var commentMeta))
        {
            var note = history.CommentRow(doctype, name, "Info", $"{user.User} approved \"{action.Action}\" ({action.Given + 1} of {action.Required})", user.User, now);
            await repository.InsertAsync(commentMeta, note, null, ct);
        }
    }

    private async Task ClearApprovalsAsync(string doctype, string name, CancellationToken ct)
    {
        if (registry.TryGet(ApprovalDocType, out var meta))
            foreach (var vote in await VotesAsync(doctype, name, null, ct))
                await repository.DeleteAsync(meta, (string)vote["name"]!, null, ct);
        await WorkflowEscalationService.ClearAsync(registry, repository, doctype, name, ct);
        await WorkflowLinkService.ClearAsync(registry, repository, doctype, name, ct);
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
                    var actions = await ActionsForAsync(workflow, meta, raw, user, ct);
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
