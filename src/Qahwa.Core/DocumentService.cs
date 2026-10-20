using System.Collections;
using System.Text.Json;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>
/// The document engine: permission checks, validation, naming, standard fields, child tables and optimistic
/// concurrency on top of an <see cref="IDocumentRepository"/>.
/// </summary>
public sealed partial class DocumentService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    PermissionService permissions,
    NamingService naming,
    TimeProvider clock,
    UserPermissionService? userPermissions = null,
    ShareStore? shares = null,
    ProfileService? profiles = null,
    HistoryRecorder? history = null,
    CollaborationHooks? collaboration = null,
    WorkflowCatalog? workflows = null,
    Lazy<AssignmentRuleEngine>? assignmentRules = null,
    Lazy<NotificationRuleEngine>? notificationRules = null,
    Lazy<WebhookService>? webhooks = null,
    Lazy<FileService>? files = null,
    Lazy<DocumentActionEngine>? documentActions = null,
    Lazy<ServerScriptEngine>? serverScripts = null,
    Lazy<AppHookRunner>? appHooks = null,
    ISecretProtector? secrets = null,
    IRealtimePublisher? realtime = null,
    ViewLogService? viewLog = null,
    AccessLogService? accessLog = null)
{
    private readonly DocumentLogic _logic = new(registry, repository, permissions, clock);

    private const int MaxLimit = 1000;

    /// <summary>The most rows one child table of a document may hold.</summary>
    public const int MaxRows = 1000;

    private static readonly Dictionary<string, FilterOp> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        ["="] = FilterOp.Eq,
        ["!="] = FilterOp.Ne,
        [">"] = FilterOp.Gt,
        ["<"] = FilterOp.Lt,
        [">="] = FilterOp.Ge,
        ["<="] = FilterOp.Le,
        ["like"] = FilterOp.Like,
        ["not like"] = FilterOp.NotLike,
        ["in"] = FilterOp.In,
        ["not in"] = FilterOp.NotIn,
        ["is"] = FilterOp.IsNull, // resolved below from the value ("set" / "not set")
    };

    public IReadOnlyList<DocTypeMeta> ListMetas(IUserContext user) =>
        registry.All
            .Where(m => !m.IsChildTable && permissions.Evaluate(m, user, PermAction.Read) != AccessLevel.None)
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public DocTypeMeta GetMeta(string doctype, IUserContext user)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Read);
        return meta;
    }

    /// <summary>The child DocTypes used by the Table fields of a DocType, keyed by name (the Desk needs them to draw grids).</summary>
    public IReadOnlyDictionary<string, DocTypeMeta> GetChildMetas(DocTypeMeta meta) =>
        meta.Fields
            .Where(f => f.Type == FieldType.Table && f.Options is not null && registry.TryGet(f.Options, out _))
            .Select(f => registry.Get(f.Options!))
            .DistinctBy(c => c.Name)
            .ToDictionary(c => c.Name, c => c, StringComparer.Ordinal);

    /// <summary>What the user may do with this DocType in general (owner-only rules count as allowed).</summary>
    public EffectivePermissions GetPermissions(DocTypeMeta meta, IUserContext user) => new(
        permissions.Evaluate(meta, user, PermAction.Read) != AccessLevel.None,
        permissions.Evaluate(meta, user, PermAction.Write) != AccessLevel.None,
        permissions.Evaluate(meta, user, PermAction.Create) != AccessLevel.None && !meta.IsChildTable,
        permissions.Evaluate(meta, user, PermAction.Delete) != AccessLevel.None,
        meta.IsSubmittable && permissions.Evaluate(meta, user, PermAction.Submit) != AccessLevel.None,
        meta.IsSubmittable && permissions.Evaluate(meta, user, PermAction.Cancel) != AccessLevel.None,
        permissions.Evaluate(meta, user, PermAction.Export) != AccessLevel.None,
        permissions.Evaluate(meta, user, PermAction.Report) != AccessLevel.None,
        permissions.Allows(meta, user, PermFlag.Import), permissions.Allows(meta, user, PermFlag.Share),
        permissions.Allows(meta, user, PermFlag.Print), permissions.Allows(meta, user, PermFlag.Email), permissions.Allows(meta, user, PermFlag.Amend));

    public async Task<Dictionary<string, object?>> InsertAsync(
        string doctype,
        IReadOnlyDictionary<string, object?> input,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        permissions.Require(meta, user, PermAction.Create);
        await EnsureModuleAsync(meta, user, ct);
        var fieldAccess = permissions.FieldAccessFor(meta, user, user.User, creating: true);
        var restrictions = await RestrictionsAsync(meta, user, ct);

        var errors = new List<FieldError>();
        var values = Coerce(meta, input, errors, meta.Autoname == "prompt", out var promptName, access: fieldAccess);
        CheckRestrictedValues(restrictions, values, errors);
        await RequireAppPermissionAsync(meta, PermAction.Create, values, user, ct);
        CheckProfileAssignment(meta, values, user, errors);
        var tables = ReadTables(meta, input, errors);
        foreach (var table in tables.Where(t => t.Rows.Count > 0 && !fieldAccess.CanWrite(t.Field)))
            errors.Add(new FieldError(table.Field.Name, "You do not have permission to fill in this table."));
        ApplyDefaults(meta, values, errors);
        foreach (var table in tables)
        {
            for (var i = 0; i < table.Rows.Count; i++)
                ApplyDefaults(table.Child, table.Rows[i].Values, errors, $"{table.Field.Name}[{i}].");
        }

        // A new document starts in the workflow's first state, whatever was sent.
        if (WorkflowOf(meta) is { } initialWorkflow)
            values[initialWorkflow.StateField] = initialWorkflow.Initial.Name;

        // Expression defaults, "fetch from" values and formulas, then validation of the finished document.
        var changed = values.Keys.ToHashSet(StringComparer.Ordinal);
        await _logic.RunAsync(meta, values, changed, tables, user, isNew: true, errors, ct);
        foreach (var key in await RunScriptsAsync(meta, values, null, isNew: true, user, errors, ct))
            changed.Add(key);
        foreach (var key in await RunAppHooksAsync(meta, values, null, isNew: true, user, errors, ct, null))
            changed.Add(key);
        var context = _logic.Context(meta, values, tables, null, user, isNew: true);
        await ValidateAsync(meta, values, values.Keys, errors, string.Empty, context, fieldAccess, ct);
        foreach (var table in tables)
            await ValidateTableAsync(table, null, errors, values, user, isNew: true, ct);
        CheckRequiredTables(meta, tables, errors, onlyIfProvided: false);
        ValidateRules(meta, tables, values, context, user, isNew: true, errors);
        await ValidateTreeAsync(meta, null, values, null, errors, ct);
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var name = meta.IsSingle ? meta.Name : await naming.NextNameAsync(meta, values, promptName, ct, user);
        var now = Now();
        values["name"] = name;
        values["creation"] = now;
        values["modified"] = now;
        values["modified_by"] = user.User;
        values["owner"] = user.User;
        values["docstatus"] = 0;
        values["idx"] = 0;

        var children = tables
            .Select(t => BuildChanges(t, meta, name, new Dictionary<string, Dictionary<string, object?>>(), now, user))
            .Where(c => !c.IsEmpty)
            .ToList();
        if (history?.Version(meta, name, "Insert", null, user.User, now) is { } created)
            children.Add(created);
        ProtectPasswords(meta, values);
        await repository.InsertAsync(meta, values, children, ct);
        await AfterSaveAsync(meta, name, values.Keys.ToHashSet(StringComparer.Ordinal), isNew: true, user, ct);
        return await LoadForAsync(meta, name, user, ct)
            ?? throw new InvalidOperationException($"{meta.Name} '{name}' vanished after insert.");
    }

    /// <summary>
    /// Runs the Validate scripts attached to the DocType (and checks the source of a Server Script being saved). What the scripts set is
    /// converted like any other input and put into <paramref name="values"/>.
    /// </summary>
    /// <returns>The names of the fields the scripts changed.</returns>
    private async Task<IReadOnlyCollection<string>> RunScriptsAsync(
        DocTypeMeta meta, Dictionary<string, object?> values, IReadOnlyDictionary<string, object?>? existing, bool isNew, IUserContext user, List<FieldError> errors, CancellationToken ct, int? transition = null)
    {
        if (meta.Name == NamingService.RuleDocType)
        {
            NamingRuleCheck.Check(registry, values, errors);
            return [];
        }

        if (meta.Name == BrandService.DocTypeName)
        {
            BrandService.Check(values, errors);
            return [];
        }

        if (meta.Name == ClientScriptService.DocTypeName)
        {
            ClientScriptService.Check(values, errors);
            return [];
        }

        if (serverScripts is null)
            return [];
        if (meta.Name == ServerScriptEngine.DocTypeName)
        {
            serverScripts.Value.CheckScript(values, errors);
            return [];
        }

        // Validate runs for every save except a cancellation (an old document that no longer passes today's rules can still be cancelled);
        // the Before Cancel scripts are the place to refuse that.
        var changed = new List<string>();
        var kinds = transition switch
        {
            2 => new[] { ServerScriptEngine.BeforeCancelEvent },
            1 => [ServerScriptEngine.ValidateEvent, ServerScriptEngine.BeforeSubmitEvent],
            _ => new[] { ServerScriptEngine.ValidateEvent },
        };
        foreach (var kind in kinds)
        {
            var changes = await serverScripts.Value.ValidateAsync(meta, values, existing, isNew, user, ct, kind);
            if (changes.Count == 0)
                continue;
            var coerced = Coerce(meta, changes, errors, false, out _);
            foreach (var (key, value) in coerced)
                values[key] = value;
            changed.AddRange(coerced.Keys);
        }

        return changed;
    }

    /// <summary>Runs the Before hooks of installed apps for this DocType, converting what they changed like any other input.</summary>
    private async Task<IReadOnlyCollection<string>> RunAppHooksAsync(
        DocTypeMeta meta, Dictionary<string, object?> values, IReadOnlyDictionary<string, object?>? existing, bool isNew, IUserContext user, List<FieldError> errors, CancellationToken ct, int? transition)
    {
        if (appHooks is null || !appHooks.Value.Has(meta.Name) || !AssignmentRuleManager.Applies(meta))
            return [];
        var changed = new List<string>();
        var events = transition switch
        {
            2 => new[] { DocEvent.BeforeCancel },
            1 => [DocEvent.BeforeSave, DocEvent.BeforeSubmit],
            _ => new[] { DocEvent.BeforeSave },
        };
        foreach (var kind in events)
        {
            var changes = await appHooks.Value.BeforeAsync(meta, kind, values, existing, isNew, user, ct);
            if (changes.Count == 0)
                continue;
            var coerced = Coerce(meta, changes, errors, false, out _);
            foreach (var (key, value) in coerced)
                values[key] = value;
            changed.AddRange(coerced.Keys);
        }

        return changed;
    }

    public async Task<Dictionary<string, object?>> GetAsync(
        string doctype,
        string name,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var (level, share) = await PreAuthorizeAsync(meta, user, PermAction.Read, name, ct);
        var doc = await LoadAsync(meta, name, ct)
            ?? (meta.IsSingle && name == meta.Name ? SingleSkeleton(meta) : throw new NotFoundException($"{meta.Name} '{name}' was not found."));
        var viaShare = Settle(level, share, PermAction.Read, doc, user);
        RequireRestrictions(await RestrictionsAsync(meta, user, ct), doc, meta);
        await RequireAppPermissionAsync(meta, PermAction.Read, doc, user, ct);
        if (viewLog is not null)
            await viewLog.RecordAsync(meta, name, user, ct);
        return Strip(meta, doc, SharedAccess(permissions.FieldAccessFor(meta, user, doc.GetValueOrDefault("owner") as string, creating: false), viaShare, share));
    }

    public Task<Dictionary<string, object?>> UpdateAsync(
        string doctype,
        string name,
        IReadOnlyDictionary<string, object?> input,
        IUserContext user,
        CancellationToken ct = default) => UpdateCoreAsync(doctype, name, input, user, transition: null, step: null, ct);

    /// <summary>Submits a draft of a submittable DocType (docstatus 0 to 1). The document is validated as for a save.</summary>
    public Task<Dictionary<string, object?>> SubmitAsync(
        string doctype, string name, IUserContext user, DateTime? modified = null, CancellationToken ct = default) =>
        UpdateCoreAsync(doctype, name, TransitionInput(modified), user, transition: 1, step: null, ct);

    /// <summary>Cancels a submitted document (docstatus 1 to 2). A cancelled document can no longer be changed.</summary>
    public Task<Dictionary<string, object?>> CancelAsync(
        string doctype, string name, IUserContext user, DateTime? modified = null, CancellationToken ct = default) =>
        UpdateCoreAsync(doctype, name, TransitionInput(modified), user, transition: 2, step: null, ct);

    private static Dictionary<string, object?> TransitionInput(DateTime? modified) =>
        modified is null ? [] : new() { ["modified"] = modified };

    private async Task AfterSaveAsync(
        DocTypeMeta meta, string name, IReadOnlySet<string> changed, bool isNew, IUserContext user, CancellationToken ct, IReadOnlyDictionary<string, object?>? before = null)
    {
        Announce(meta, name, user, deleted: false);
        if (profiles is not null && await LoadAsync(meta, name, ct) is { } saved)
            await profiles.AfterSaveAsync(meta, saved, changed, isNew, ct);
        if (collaboration is not null)
            await collaboration.AfterSaveAsync(meta, name, isNew, user, ct);
        if (assignmentRules is not null && AssignmentRuleManager.Applies(meta) && await LoadAsync(meta, name, ct) is { } current)
            await assignmentRules.Value.AfterSaveAsync(meta, name, current, condition => ConditionHolds(condition, meta, current, user), user, ct);
        if (notificationRules is not null && AssignmentRuleManager.Applies(meta) && await LoadAsync(meta, name, ct) is { } latest)
            await notificationRules.Value.AfterSaveAsync(meta, name, isNew, before, latest, user, ct);
        if (webhooks is not null && AssignmentRuleManager.Applies(meta) && await LoadAsync(meta, name, ct) is { } hooked)
            await webhooks.Value.AfterSaveAsync(meta, name, isNew, before, hooked, ct);
        if (documentActions is not null && meta.Name == DocumentActionEngine.DocTypeName)
            documentActions.Value.Invalidate();
        if (documentActions is not null && AssignmentRuleManager.Applies(meta) && await LoadAsync(meta, name, ct) is { } acted)
            await documentActions.Value.AfterSaveAsync(meta, name, isNew, before, acted, user, ct);
        if (serverScripts is not null && meta.Name == ServerScriptEngine.DocTypeName)
            serverScripts.Value.Invalidate();
        if (serverScripts is not null && AssignmentRuleManager.Applies(meta) && await LoadAsync(meta, name, ct) is { } scripted)
            await serverScripts.Value.AfterSaveAsync(meta, name, isNew, before, scripted, user, ct);
        if (appHooks is not null && appHooks.Value.Has(meta.Name) && AssignmentRuleManager.Applies(meta) && await LoadAsync(meta, name, ct) is { } hooked2)
        {
            var oldStatus = Convert.ToInt32(before?.GetValueOrDefault("docstatus") ?? 0);
            var newStatus = Convert.ToInt32(hooked2.GetValueOrDefault("docstatus") ?? 0);
            await appHooks.Value.AfterAsync(meta, DocEvent.AfterSave, name, isNew, before, hooked2, user, ct);
            if (!isNew && oldStatus == 0 && newStatus == 1)
                await appHooks.Value.AfterAsync(meta, DocEvent.AfterSubmit, name, isNew, before, hooked2, user, ct);
            else if (!isNew && oldStatus == 1 && newStatus == 2)
                await appHooks.Value.AfterAsync(meta, DocEvent.AfterCancel, name, isNew, before, hooked2, user, ct);
        }
    }

    private static DocumentValidationException General(string message) => new([new FieldError(string.Empty, message)]);

    private async Task<Dictionary<string, object?>> UpdateCoreAsync(
        string doctype,
        string name,
        IReadOnlyDictionary<string, object?> input,
        IUserContext user,
        int? transition,
        WorkflowStep? step,
        CancellationToken ct)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        // With a workflow, submitting and cancelling are steps of the workflow; a workflow step needs to read the document, not write it.
        if (step is null && transition is not null && WorkflowOf(meta) is not null)
            throw General($"{meta.Name} uses a workflow: use its actions to submit or cancel.");
        if (step is not null)
            transition = step.DocStatus;
        var action = step is not null ? PermAction.Read : transition switch { 1 => PermAction.Submit, 2 => PermAction.Cancel, _ => PermAction.Write };
        var (level, share) = await PreAuthorizeAsync(meta, user, action, name, ct);
        var existing = await repository.GetAsync(meta, name, ct);
        if (existing is null && meta.IsSingle && name == meta.Name && transition is null)
        {
            // A single DocType has no document until it is first saved; saving creates it.
            return await InsertAsync(doctype, input, user, ct);
        }

        if (existing is null)
            throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        var viaShare = Settle(level, share, action, existing, user);

        var status = Convert.ToInt32(existing.GetValueOrDefault("docstatus") ?? 0);
        if (transition is not null && !meta.IsSubmittable)
            throw General($"{meta.Name} is not a submittable DocType.");
        if (transition == 1 && status != 0)
            throw General("Only a draft can be submitted.");
        if (transition == 2 && status != 1)
            throw General("Only a submitted document can be cancelled.");
        if (transition is null && meta.IsSubmittable && status == 2)
            throw General("A cancelled document cannot be changed.");
        var restrictions = await RestrictionsAsync(meta, user, ct);
        RequireRestrictions(restrictions, existing, meta);
        await RequireAppPermissionAsync(meta, transition switch { 1 => PermAction.Submit, 2 => PermAction.Cancel, _ => PermAction.Write }, existing, user, ct);
        var fieldAccess = SharedAccess(permissions.FieldAccessFor(meta, user, existing.GetValueOrDefault("owner") as string, creating: false), viaShare, share);

        var errors = new List<FieldError>();
        var changes = Coerce(meta, input, errors, allowName: false, out _);
        var tables = ReadTables(meta, input, errors);

        // Only what really differs from the saved document counts as a change.
        foreach (var key in changes.Keys.ToList())
        {
            if (Equals(existing.GetValueOrDefault(key), changes[key]))
                changes.Remove(key);
        }

        CheckRestrictedValues(restrictions, changes, errors);
        CheckProfileAssignment(meta, changes, user, errors);

        // A workflow owns its state field, and can limit who edits a document in each state.
        if (WorkflowOf(meta) is { } workflow)
        {
            if (step is null && changes.ContainsKey(workflow.StateField))
                errors.Add(new FieldError(workflow.StateField, "Use the workflow actions to change this."));
            if (step is not null)
                changes[workflow.StateField] = step.State;
            if (step is null)
            {
                var state = StateOf(workflow, existing);
                if (state.EditRoles.Count > 0 && !HoldsRole(user, state.EditRoles) && (changes.Count > 0 || tables.Any(t => t.Provided)))
                    errors.Add(new FieldError(string.Empty, $"In the state '{state.Name}' this can only be edited by: {string.Join(", ", state.EditRoles)}."));
            }
        }

        // A "set once" field keeps the first value it was given.
        foreach (var key in changes.Keys)
        {
            if (meta.Fields.FirstOrDefault(f => f.Name == key) is { SetOnce: true } && existing.GetValueOrDefault(key) is not (null or ""))
                errors.Add(new FieldError(key, "This field can only be set once and cannot be changed."));
        }

        // After submit only the fields marked "allow on submit" may change.
        if (transition is null && step is null && meta.IsSubmittable && status == 1)
        {
            foreach (var key in changes.Keys)
            {
                if (meta.Fields.FirstOrDefault(f => f.Name == key) is { AllowOnSubmit: false })
                    errors.Add(new FieldError(key, "This field cannot be changed after the document is submitted."));
            }
        }

        // A client may echo back values it was shown; it may not change fields above its permission level.
        foreach (var key in changes.Keys)
        {
            var field = meta.Fields.FirstOrDefault(f => f.Name == key);
            // A workflow step sets the state itself; the person only needs to be allowed to take the step.
            if (field is not null && !fieldAccess.CanWrite(field) && !(step is not null && key == WorkflowOf(meta)?.StateField))
                errors.Add(new FieldError(key, "You do not have permission to change this field."));
        }

        // Optimistic concurrency: if the client says which version it edited, it must still be current.
        var stored = existing.GetValueOrDefault("modified") as DateTime?;
        if (input.TryGetValue("modified", out var rawModified) && FieldValueConverter.Unwrap(rawModified) is not null)
        {
            try
            {
                if (FieldValueConverter.ToStorage(FieldType.Datetime, rawModified) as DateTime? != stored)
                    throw new ConflictException(
                        $"{meta.Name} '{name}' was modified after you loaded it. Reload and try again.");
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError("modified", ex.Message));
            }
        }

        var merged = new Dictionary<string, object?>(existing);
        foreach (var (key, value) in changes)
            merged[key] = value;

        // Rows the client may refer to by name: the ones this document already has in each table.
        var currentRows = new Dictionary<string, Dictionary<string, Dictionary<string, object?>>>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            var rows = await repository.GetChildrenAsync(table.Child, meta.Name, name, table.Field.Name, ct);
            currentRows[table.Field.Name] = rows.ToDictionary(r => (string)r["name"]!, r => r, StringComparer.Ordinal);
            foreach (var row in table.Rows.Where(r => r.Name is not null && currentRows[table.Field.Name].ContainsKey(r.Name)))
                row.Existing = currentRows[table.Field.Name][row.Name!];
        }

        // Formulas and rules may read tables the client did not send: bring their saved rows into play unchanged.
        if (meta.HasLogic() || TableFields(meta).Any(f => registry.Get(f.Options!).HasLogic()))
        {
            foreach (var field in TableFields(meta).Where(f => tables.All(t => t.Field.Name != f.Name)).ToList())
            {
                var child = registry.Get(field.Options!);
                var rows = await repository.GetChildrenAsync(child, meta.Name, name, field.Name, ct);
                currentRows[field.Name] = rows.ToDictionary(r => (string)r["name"]!, r => r, StringComparer.Ordinal);
                tables.Add(new TableInput(field, child, rows.Select(r => new RowInput((string)r["name"]!, [], r)).ToList(), Provided: false));
            }
        }

        var changed = changes.Keys.ToHashSet(StringComparer.Ordinal);
        await _logic.RunAsync(meta, merged, changed, tables, user, isNew: false, errors, ct);
        foreach (var key in await RunScriptsAsync(meta, merged, existing, isNew: false, user, errors, ct, transition))
            changed.Add(key);
        foreach (var key in await RunAppHooksAsync(meta, merged, existing, isNew: false, user, errors, ct, transition))
            changed.Add(key);
        foreach (var key in changed)
            changes[key] = merged[key]; // calculated, fetched and script-set values are saved too

        var context = _logic.Context(meta, merged, tables, null, user, isNew: false);
        await ValidateAsync(meta, merged, changed, errors, string.Empty, context, fieldAccess, ct);
        foreach (var table in tables.Where(t => t.Provided))
            await ValidateTableAsync(table, currentRows[table.Field.Name], errors, merged, user, isNew: false, ct);

        foreach (var table in tables.Where(t => t.Provided && !fieldAccess.CanWrite(t.Field)))
        {
            var attempted = BuildChanges(table, meta, name, currentRows[table.Field.Name], Now(), user);
            if (!attempted.IsEmpty)
                errors.Add(new FieldError(table.Field.Name, "You do not have permission to change this table."));
        }

        if (transition is null && meta.IsSubmittable && status == 1)
        {
            foreach (var table in tables.Where(t => t.Provided && !t.Field.AllowOnSubmit))
            {
                if (!BuildChanges(table, meta, name, currentRows[table.Field.Name], Now(), user).IsEmpty)
                    errors.Add(new FieldError(table.Field.Name, "This table cannot be changed after the document is submitted."));
            }
        }

        CheckRequiredTables(meta, tables.Where(t => t.Provided).ToList(), errors, onlyIfProvided: true);
        ValidateRules(meta, tables, merged, context, user, isNew: false, errors);
        await ValidateTreeAsync(meta, name, merged, existing, errors, ct);
        if (transition == 2)
            errors.Clear(); // an old document that no longer passes today's rules can still be cancelled
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        var now = Now();
        var built = tables
            .Select(t => (Table: t, Change: BuildChanges(t, meta, name, currentRows[t.Field.Name], now, user)))
            .Where(x => !x.Change.IsEmpty)
            .ToList();
        var children = built.Select(x => x.Change).ToList();
        if (transition is not null)
            changes["docstatus"] = transition.Value;
        if (changes.Count == 0 && children.Count == 0)
            return (await LoadForAsync(meta, name, user, ct))!;

        changes["modified"] = now;
        changes["modified_by"] = user.User;
        if (history is not null)
        {
            var eventName = transition switch { 1 => "Submit", 2 => "Cancel", _ => "Update" };
            var data = history.UpdateData(meta, existing, changes, built.Select(x => (x.Table.Field, x.Table.Child, x.Change)).ToList(), currentRows);
            if (history.Version(meta, name, eventName, data, user.User, now) is { } logged)
                children.Add(logged);
        }

        ProtectPasswords(meta, changes);
        if (!await repository.UpdateAsync(meta, name, changes, stored, children, ct))
            throw new ConflictException($"{meta.Name} '{name}' was changed or deleted by someone else. Reload and try again.");

        await AfterSaveAsync(meta, name, changes.Keys.ToHashSet(StringComparer.Ordinal), isNew: false, user, ct, before: existing);
        return await LoadForAsync(meta, name, user, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
    }

    public async Task DeleteAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        await EnsureModuleAsync(meta, user, ct);
        if (meta.IsSingle)
            throw General($"{meta.Name} has a single document and it cannot be deleted.");
        var access = permissions.Require(meta, user, PermAction.Delete);
        var existing = await repository.GetAsync(meta, name, ct)
            ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        permissions.RequireOwnership(access, existing, user);
        RequireRestrictions(await RestrictionsAsync(meta, user, ct), existing, meta);
        await RequireAppPermissionAsync(meta, PermAction.Delete, existing, user, ct);
        if (meta.IsSubmittable && Convert.ToInt32(existing.GetValueOrDefault("docstatus") ?? 0) == 1)
            throw General("A submitted document cannot be deleted. Cancel it first.");
        if (meta.IsTree && await HasChildrenAsync(meta, name, ct))
            throw General($"'{name}' has child nodes. Move or delete them first.");

        if (serverScripts is not null && AssignmentRuleManager.Applies(meta))
            await serverScripts.Value.BeforeDeleteAsync(meta, existing, user, ct);
        if (appHooks is not null && appHooks.Value.Has(meta.Name) && AssignmentRuleManager.Applies(meta))
            await appHooks.Value.BeforeAsync(meta, DocEvent.BeforeDelete, existing, existing, isNew: false, user, ct);

        var childTables = TableFields(meta).Select(f => registry.Get(f.Options!)).ToList();
        List<ChildChanges>? log = null;
        if (history is not null && history.Tracks(meta) && await LoadAsync(meta, name, ct) is { } snapshot
            && history.Version(meta, name, "Delete", history.DeleteData(meta, snapshot), user.User, history.Now()) is { } deleted)
        {
            log = [deleted];
        }

        // A copy is kept (in the same transaction) so a System Manager can restore the document.
        if (DeletedDocumentService.Keeps(meta) && await LoadAsync(meta, name, ct) is { } copyOf
            && DeletedDocumentService.Capture(registry, meta, copyOf, user.User, Now()) is { } kept)
        {
            (log ??= []).Add(kept);
        }

        if (!await repository.DeleteAsync(meta, name, childTables, ct, log))
            throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        if (webhooks is not null)
            await webhooks.Value.AfterDeleteAsync(meta, name, existing, ct);
        if (documentActions is not null && meta.Name == DocumentActionEngine.DocTypeName)
            documentActions.Value.Invalidate();
        if (serverScripts is not null && meta.Name == ServerScriptEngine.DocTypeName)
            serverScripts.Value.Invalidate();
        if (appHooks is not null && appHooks.Value.Has(meta.Name) && AssignmentRuleManager.Applies(meta))
            await appHooks.Value.AfterAsync(meta, DocEvent.AfterDelete, name, false, existing, existing, user, ct);
        Announce(meta, name, user, deleted: true);
        if (files is not null)
            await files.Value.DeleteForDocumentAsync(meta.Name, name, ct);
        if (shares is not null)
            await shares.DeleteForDocumentAsync(meta.Name, name, ct);
        if (history is not null)
            await history.DeleteCommentsAsync(meta.Name, name, ct);
        if (collaboration is not null)
            await collaboration.DeleteForDocumentAsync(meta.Name, name, ct);
    }

    public async Task<ListResult> ListAsync(
        string doctype,
        ListRequest request,
        IUserContext user,
        CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var query = await PrepareListQueryAsync(meta, request, user, ct);
        var rows = await repository.ListAsync(meta, query, ct);
        var total = await repository.CountAsync(meta, query, ct);
        return new ListResult(rows.ToList(), total);
    }

    /// <summary>
    /// The request as it will really run for this person: validated, and limited by every layer that decides which documents
    /// they may see (own-only roles, shares, User Permissions, blocked modules). Lists and counts both start here.
    /// </summary>
    private async Task<ListQuery> PrepareListQueryAsync(DocTypeMeta meta, ListRequest request, IUserContext user, CancellationToken ct)
    {
        await EnsureModuleAsync(meta, user, ct);
        var access = permissions.Evaluate(meta, user, PermAction.Read);
        var sharedNames = access == AccessLevel.All || shares is null
            ? []
            : (await shares.ForUserAsync(meta.Name, user, ct)).Where(kv => kv.Value.Allows(PermAction.Read)).Select(kv => kv.Key).ToList();
        if (access == AccessLevel.None && sharedNames.Count == 0)
            permissions.Require(meta, user, PermAction.Read); // throws
        var labels = request.Filters.Where(LabelService.IsLabelFilter).ToList();
        if (labels.Count > 0)
            request = request with { Filters = request.Filters.Where(f => !LabelService.IsLabelFilter(f)).ToList() };
        var query = Normalize(meta, request, permissions.FieldAccessFor(meta, user, documentOwner: null, creating: false), user);
        if (labels.Count > 0)
            query = query with { Filters = [.. query.Filters, .. labels.Select(f => LabelService.ToQueryFilter(registry, meta.Name, f, user))] };

        if (access == AccessLevel.None)
            query = query with { Filters = [.. query.Filters, new QueryFilter("name", FilterOp.In, sharedNames.Cast<object?>().ToList())] };
        else if (access == AccessLevel.Owner)
            query = query with { Filters = [.. query.Filters, new QueryFilter("owner", sharedNames.Count > 0 ? FilterOp.OwnerOrNames : FilterOp.Eq, sharedNames.Count > 0 ? new OwnerOrNames(user.User, sharedNames) : user.User)] };

        if (appHooks is not null && AssignmentRuleManager.Applies(meta) && !permissions.IsAdministrator(user) && appHooks.Value.HasPermissionRules(meta.Name))
            query = query with { Filters = [.. query.Filters, .. await appHooks.Value.FiltersAsync(meta, user, ct)] };

        foreach (var restriction in await RestrictionsAsync(meta, user, ct))
        {
            query = query with
            {
                Filters = [.. query.Filters, new QueryFilter(restriction.Field, restriction.AllowEmpty ? FilterOp.InOrNull : FilterOp.In, restriction.Allowed.Cast<object?>().ToList())],
            };
        }

        return query;
    }

    /// <summary>
    /// The user's role-based access, and what the document has been shared with them. Throws when neither lets them
    /// act (before the document is loaded, so a missing document and a forbidden one look alike).
    /// </summary>
    private async Task<(AccessLevel Level, ShareGrant Share)> PreAuthorizeAsync(
        DocTypeMeta meta, IUserContext user, PermAction action, string name, CancellationToken ct)
    {
        await EnsureModuleAsync(meta, user, ct);
        var level = permissions.Evaluate(meta, user, action);
        var share = level == AccessLevel.All || shares is null ? ShareGrant.None : await shares.ForDocumentAsync(meta.Name, name, user, ct);
        if (level == AccessLevel.None && !share.Allows(action))
            permissions.Require(meta, user, action); // throws
        return (level, share);
    }

    /// <summary>Checks ownership for owner-only access; returns true when the access exists only because of a share.</summary>
    private static bool Settle(AccessLevel level, ShareGrant share, PermAction action, IReadOnlyDictionary<string, object?> doc, IUserContext user)
    {
        if (level == AccessLevel.All)
            return false;
        if (level == AccessLevel.Owner && string.Equals(doc.GetValueOrDefault("owner") as string, user.User, StringComparison.Ordinal))
            return false;
        if (share.Allows(action))
            return true;

        throw new PermissionDeniedException("You can only access documents you own.");
    }

    /// <summary>A share opens level 0 of the document: reading it, and writing it when the share says so.</summary>
    private static FieldAccess SharedAccess(FieldAccess access, bool viaShare, ShareGrant share) =>
        !viaShare ? access : new FieldAccess(access.Readable.Append(0).ToHashSet(), share.Write ? access.Writable.Append(0).ToHashSet() : access.Writable);

    private async Task<IReadOnlyList<Restriction>> RestrictionsAsync(DocTypeMeta meta, IUserContext user, CancellationToken ct) =>
        userPermissions is null ? [] : await userPermissions.ForAsync(meta, user, ct);

    /// <summary>A document outside the user's User Permissions might as well not exist for them.</summary>
    private static void RequireRestrictions(IReadOnlyList<Restriction> restrictions, IReadOnlyDictionary<string, object?> doc, DocTypeMeta meta)
    {
        if (restrictions.Any(r => !r.Admits(doc.GetValueOrDefault(r.Field))))
            throw new PermissionDeniedException($"You are not allowed to access this {meta.Name}.");
    }

    /// <summary>The user may not point a Link field at a document their User Permissions rule out.</summary>
    private static void CheckRestrictedValues(IReadOnlyList<Restriction> restrictions, IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        foreach (var restriction in restrictions.Where(r => !r.IsName))
        {
            if (values.TryGetValue(restriction.Field, out var value) && value is string { Length: > 0 } text && !restriction.Allowed.Contains(text))
                errors.Add(new FieldError(restriction.Field, $"You are not allowed to use '{text}'."));
        }
    }

    /// <summary>Child tables are only reachable through their parent document, so they look like they do not exist.</summary>
    private static DocTypeMeta RequireTopLevel(DocTypeMeta meta) =>
        meta.IsChildTable ? throw new NotFoundException($"DocType '{meta.Name}' was not found.") : meta;

    private static IEnumerable<DocFieldMeta> TableFields(DocTypeMeta meta) =>
        meta.Fields.Where(f => f.Type == FieldType.Table && f.Options is not null);

    /// <summary>The document as this user may see it: rows attached, fields above their permission level removed.</summary>
    private async Task<Dictionary<string, object?>?> LoadForAsync(DocTypeMeta meta, string name, IUserContext user, CancellationToken ct)
    {
        var doc = await LoadAsync(meta, name, ct);
        return doc is null
            ? null
            : Strip(meta, doc, permissions.FieldAccessFor(meta, user, doc.GetValueOrDefault("owner") as string, creating: false));
    }

    /// <summary>Removes the fields the user may not read. They are absent, not empty, so their existence is not revealed.</summary>
    private static Dictionary<string, object?> Strip(DocTypeMeta meta, Dictionary<string, object?> doc, FieldAccess access)
    {
        MaskPasswords(meta, doc);
        if (access.IsUnrestricted)
            return doc;

        foreach (var field in meta.Fields.Where(f => !access.CanRead(f)))
            doc.Remove(field.Name);
        return doc;
    }

    /// <summary>
    /// What the Desk needs to draw a DocType for this user: only the fields they may read, their effective permissions,
    /// the child DocTypes of its tables, and which field levels they may edit. Permission rules are not disclosed.
    /// </summary>
    public MetaView GetMetaView(string doctype, IUserContext user)
    {
        var meta = GetMeta(doctype, user);
        var access = permissions.FieldAccessFor(meta, user, user.User, creating: false);
        var visible = access.IsUnrestricted
            ? meta
            : meta with { Fields = meta.Fields.Where(access.CanRead).ToList(), TitleField = meta.Fields.Any(f => f.Name == meta.TitleField && !access.CanRead(f)) ? null : meta.TitleField };

        return new MetaView(
            visible with { Permissions = [] },
            GetPermissions(meta, user),
            GetChildMetas(meta),
            access.Writable.Order().ToList());
    }

    /// <summary>Like <see cref="GetMetaView"/>, but a person with no role access can still open a DocType through documents shared with them.</summary>
    public async Task<MetaView> GetMetaViewAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        await EnsureModuleAsync(meta, user, ct);
        if (permissions.Evaluate(meta, user, PermAction.Read) != AccessLevel.None || shares is null)
            return GetMetaView(doctype, user);

        var grants = await shares.ForUserAsync(meta.Name, user, ct);
        if (grants.Count == 0)
            return GetMetaView(doctype, user); // throws the usual permission error

        // Which documents they may write is decided per document; the form is editable only where the server allows it.
        var access = permissions.FieldAccessFor(meta, user, null, creating: false);
        var visible = meta with { Fields = meta.Fields.Where(f => f.PermLevel == 0 || access.CanRead(f)).ToList(), Permissions = [] };
        return new MetaView(
            visible,
            new EffectivePermissions(true, grants.Values.Any(g => g.Write), false, false),
            GetChildMetas(meta),
            [0]);
    }

    /// <summary>The document with its child rows attached under the Table field names.</summary>
    private async Task<Dictionary<string, object?>?> LoadAsync(DocTypeMeta meta, string name, CancellationToken ct)
    {
        var doc = await repository.GetAsync(meta, name, ct);
        if (doc is null)
            return null;

        foreach (var field in TableFields(meta))
        {
            var child = registry.Get(field.Options!);
            var rows = await repository.GetChildrenAsync(child, meta.Name, name, field.Name, ct);
            doc[field.Name] = rows.Select(r => ShapeRow(child, r)).ToList();
        }

        return doc;
    }

    /// <summary>A child row as the API shows it: its id, position and the DocType's own fields.</summary>
    private static Dictionary<string, object?> ShapeRow(DocTypeMeta child, Dictionary<string, object?> row)
    {
        var shaped = new Dictionary<string, object?> { ["name"] = row["name"], ["idx"] = row["idx"] };
        foreach (var field in child.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
            shaped[field.Name] = row.GetValueOrDefault(field.Name);
        return shaped;
    }

    private ListQuery Normalize(DocTypeMeta meta, ListRequest request, FieldAccess access, IUserContext? asker = null)
    {
        var moment = clock.GetUtcNow().UtcDateTime;
        var columns = meta.ColumnTypes();
        var errors = new List<FieldError>();

        // Fields above the user's permission level cannot be shown, filtered on or sorted by: any of those would reveal them.
        var hidden = meta.Fields.Where(f => !access.CanRead(f) || f.Type == FieldType.Password).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);

        List<string>? fields = null;
        if (request.Fields is { Count: > 0 })
        {
            fields = ["name"];
            foreach (var field in request.Fields.Where(f => f != "name").Distinct(StringComparer.Ordinal))
            {
                if (hidden.Contains(field))
                    errors.Add(new FieldError(field, "You do not have permission to read this field."));
                else if (columns.ContainsKey(field))
                    fields.Add(field);
                else
                    errors.Add(new FieldError(field, "Unknown field."));
            }
        }
        else if (hidden.Count > 0)
        {
            fields = columns.Keys.Where(c => !hidden.Contains(c)).ToList();
        }

        var filters = new List<QueryFilter>();
        foreach (var raw in request.Filters)
        {
            if (hidden.Contains(raw.Field))
            {
                errors.Add(new FieldError(raw.Field, "You do not have permission to filter on this field."));
                continue;
            }

            if (!columns.TryGetValue(raw.Field, out var type))
            {
                errors.Add(new FieldError(raw.Field, "Cannot filter on an unknown field."));
                continue;
            }

            var opName = raw.Op.Trim().ToLowerInvariant();
            if (!Operators.ContainsKey(opName) && opName is not ("between" or "timespan"))
            {
                errors.Add(new FieldError(raw.Field, $"Unsupported filter operator '{raw.Op}'."));
                continue;
            }

            try
            {
                // "__user" and "__today" first, then "between" and "timespan" become bounds, then the usual conversion for the field's type.
                var withWords = raw with { Op = opName, Value = FilterExpansion.Dynamic(raw.Value, asker?.User, moment) };
                foreach (var part in FilterExpansion.Expand(withWords, type, moment))
                    filters.Add(BuildFilter(part, type, Operators[part.Op]));
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(raw.Field, ex.Message));
            }
        }

        string? orderBy = request.OrderBy;
        if (orderBy is not null && hidden.Contains(orderBy))
        {
            errors.Add(new FieldError(orderBy, "You do not have permission to sort by this field."));
            orderBy = null;
        }
        else if (orderBy is not null && !columns.ContainsKey(orderBy))
        {
            errors.Add(new FieldError(orderBy, "Cannot order by an unknown field."));
            orderBy = null;
        }

        if (errors.Count > 0)
            throw new DocumentValidationException(errors);

        return new ListQuery
        {
            Filters = filters,
            Fields = fields,
            OrderBy = orderBy,
            Descending = request.Descending,
            Limit = Math.Clamp(request.Limit, 1, MaxLimit),
            Offset = Math.Max(request.Offset, 0),
        };
    }

    private static QueryFilter BuildFilter(RawFilter raw, FieldType type, FilterOp op)
    {
        switch (op)
        {
            case FilterOp.IsNull:
                var state = (FieldValueConverter.Unwrap(raw.Value) as string)?.Trim().ToLowerInvariant();
                return state switch
                {
                    "set" or "not null" => new QueryFilter(raw.Field, FilterOp.IsNotNull),
                    "not set" or "null" => new QueryFilter(raw.Field, FilterOp.IsNull),
                    _ => throw new FormatException("Use 'set' or 'not set' with the 'is' operator."),
                };

            case FilterOp.In or FilterOp.NotIn:
                var items = FieldValueConverter.UnwrapList(raw.Value)
                    .Select(v => FieldValueConverter.ToStorage(type, v))
                    .ToList();
                return new QueryFilter(raw.Field, op, items);

            case FilterOp.Like or FilterOp.NotLike:
                if (!FieldTypes.IsTextual(type))
                    throw new FormatException("'like' only works on text fields.");
                return new QueryFilter(raw.Field, op, FieldValueConverter.ToStorage(FieldType.Data, raw.Value));

            default:
                var value = FieldValueConverter.ToStorage(type, raw.Value);
                if (value is null)
                    throw new FormatException("A value is required for this operator.");
                return new QueryFilter(raw.Field, op, value);
        }
    }

    /// <summary>Converts client input to storage types. Unknown or non-data fields become validation errors.</summary>
    private static Dictionary<string, object?> Coerce(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> input,
        List<FieldError> errors,
        bool allowName,
        out string? providedName,
        string pathPrefix = "",
        FieldAccess? access = null)
    {
        providedName = null;
        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, raw) in input)
        {
            if (key == "name" && allowName)
            {
                providedName = FieldValueConverter.Unwrap(raw) as string;
                continue;
            }

            // Standard fields are managed by the engine; clients may echo them back harmlessly.
            if (StandardFields.IsReserved(key))
                continue;

            var field = meta.Fields.FirstOrDefault(f => f.Name == key);
            if (field is null)
            {
                errors.Add(new FieldError(pathPrefix + key, "Unknown field."));
                continue;
            }

            if (field.Type == FieldType.Table)
                continue; // child rows are read separately by ReadTables

            // The link to an amended document is only ever set by Amend, never by what a client sends.
            if (key == AmendedFromField)
                continue;

            // The form shows a set password as the mask; sending the mask back means "leave it as it is".
            if (field.Type == FieldType.Password && FieldValueConverter.Unwrap(raw) as string == PasswordMask)
                continue;

            // A field above the person's permission level cannot be filled in; empty values (an echo) are harmless.
            if (access is not null && !access.CanWrite(field))
            {
                if (FieldValueConverter.Unwrap(raw) is not (null or ""))
                    errors.Add(new FieldError(pathPrefix + key, "You do not have permission to set this field."));
                continue;
            }

            if (!FieldTypes.HasColumn(field.Type))
            {
                errors.Add(new FieldError(pathPrefix + key, $"{field.Type} fields cannot be set directly yet."));
                continue;
            }

            try
            {
                var value = FieldValueConverter.ToStorage(field.Type, raw);
                if (field.NonNegative && value is sbyte or short or int or long or float or double or decimal && Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture) < 0)
                {
                    errors.Add(new FieldError(pathPrefix + key, "This value cannot be negative."));
                    continue;
                }

                if (field.MaxLength is { } max && value is string long_ && long_.Length > max)
                {
                    errors.Add(new FieldError(pathPrefix + key, $"Use at most {max} characters."));
                    continue;
                }

                values[key] = field.Lowercase && value is string text ? text.ToLowerInvariant() : value;
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(pathPrefix + key, ex.Message));
            }
        }

        return values;
    }

    /// <summary>Reads the rows the client sent for each Table field it mentioned (a missing field means "unchanged").</summary>
    private List<TableInput> ReadTables(DocTypeMeta meta, IReadOnlyDictionary<string, object?> input, List<FieldError> errors)
    {
        var tables = new List<TableInput>();
        foreach (var field in TableFields(meta))
        {
            if (!input.TryGetValue(field.Name, out var raw))
                continue;

            if (!registry.TryGet(field.Options!, out var child))
            {
                errors.Add(new FieldError(field.Name, $"The child DocType '{field.Options}' does not exist."));
                continue;
            }

            var rawRows = ReadRows(raw, field.Name, errors);
            if (rawRows.Count > MaxRows)
            {
                errors.Add(new FieldError(field.Name, $"A table can hold at most {MaxRows} rows."));
                continue;
            }

            var rows = new List<RowInput>();
            for (var i = 0; i < rawRows.Count; i++)
            {
                var values = Coerce(child, rawRows[i], errors, allowName: true, out var rowName, $"{field.Name}[{i}].");
                rows.Add(new RowInput(rowName, values));
            }

            tables.Add(new TableInput(field, child, rows));
        }

        return tables;
    }

    private static List<Dictionary<string, object?>> ReadRows(object? raw, string path, List<FieldError> errors)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (raw is JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } || raw is null)
            return rows;

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            var index = 0;
            foreach (var element in array.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Object)
                {
                    rows.Add(element.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone(), StringComparer.Ordinal));
                }
                else
                {
                    errors.Add(new FieldError($"{path}[{index}]", "Each row must be an object."));
                }

                index++;
            }

            return rows;
        }

        if (raw is IEnumerable items and not string)
        {
            var index = 0;
            foreach (var item in items)
            {
                if (item is IEnumerable<KeyValuePair<string, object?>> pairs)
                    rows.Add(pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal));
                else if (item is IEnumerable<KeyValuePair<string, JsonElement>> jsonPairs)
                    rows.Add(jsonPairs.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.Ordinal));
                else
                    errors.Add(new FieldError($"{path}[{index}]", "Each row must be an object."));
                index++;
            }

            return rows;
        }

        errors.Add(new FieldError(path, "Must be a list of rows."));
        return rows;
    }

    /// <summary>Defaults starting with "=" are expressions; <see cref="DocumentLogic"/> evaluates those.</summary>
    private static bool IsExpressionDefault(string? value) => value is { Length: > 1 } && value[0] == '=';

    private static void ApplyDefaults(
        DocTypeMeta meta, Dictionary<string, object?> values, List<FieldError> errors, string pathPrefix = "")
    {
        foreach (var field in meta.Fields.Where(f => f.Default is not null && !IsExpressionDefault(f.Default) && FieldTypes.HasColumn(f.Type)))
        {
            if (values.TryGetValue(field.Name, out var present) && present is not (null or ""))
                continue; // an empty value counts as "not given"

            try
            {
                values[field.Name] = FieldValueConverter.ToStorage(field.Type, field.Default);
            }
            catch (FormatException ex)
            {
                errors.Add(new FieldError(pathPrefix + field.Name, $"Invalid default: {ex.Message}"));
            }
        }
    }

    private async Task ValidateAsync(
        DocTypeMeta meta,
        IReadOnlyDictionary<string, object?> document,
        IEnumerable<string> changed,
        List<FieldError> errors,
        string pathPrefix,
        ExpressionContext? context,
        FieldAccess? access,
        CancellationToken ct)
    {
        var changedSet = changed.ToHashSet(StringComparer.Ordinal);

        foreach (var field in meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)))
        {
            var path = pathPrefix + field.Name;
            var value = document.GetValueOrDefault(field.Name);
            var isEmpty = value is null or "";

            // Required by the field or by its condition, and only while the field is shown.
            if (DocumentLogic.IsRequired(field, context) && isEmpty && (access is null || access.CanWrite(field)))
            {
                errors.Add(new FieldError(path, "This field is required."));
                continue;
            }

            if (isEmpty || !changedSet.Contains(field.Name))
                continue;

            if (value is string text && FieldTypes.IsShortText(field.Type) && text.Length > FieldTypes.ShortTextLength)
                errors.Add(new FieldError(path, $"Value is too long (max {FieldTypes.ShortTextLength} characters)."));

            if (field.Type == FieldType.Select && !field.SelectOptions().Contains(value as string, StringComparer.Ordinal))
                errors.Add(new FieldError(path, $"Must be one of: {string.Join(", ", field.SelectOptions())}."));

            if (field.Type is FieldType.Attach or FieldType.AttachImage && value is string fileId)
            {
                // Files are attached to a saved document first; the field then names one of them.
                var problem = pathPrefix.Length > 0 ? "Attachments cannot be inside a table."
                    : files is null ? null
                    : await files.Value.CheckFieldFileAsync(fileId, meta.Name, document.GetValueOrDefault("name") as string, field.Type == FieldType.AttachImage, ct);
                if (problem is not null)
                    errors.Add(new FieldError(path, problem));
            }
            else if (field.Type == FieldType.Link && field.Options == DocTypeTarget && value is string named)
            {
                if (!registry.TryGet(named, out var known) || known.IsChildTable)
                    errors.Add(new FieldError(path, $"DocType '{named}' does not exist."));
            }
            else if (field.Type == FieldType.DynamicLink && value is string dynamicName)
            {
                // The document it points at is looked up in the DocType the sibling field names (a Link to "DocType").
                var typeField = field.Options ?? string.Empty;
                if (FieldValueConverter.Unwrap(document.GetValueOrDefault(typeField)) is not string typeName || string.IsNullOrEmpty(typeName))
                    errors.Add(new FieldError(path, "Choose the kind of document first."));
                else if (!registry.TryGet(typeName, out var dynamicTarget) || dynamicTarget.IsChildTable)
                    errors.Add(new FieldError(path, $"DocType '{typeName}' does not exist."));
                else if (!await repository.ExistsAsync(dynamicTarget, dynamicName, ct))
                    errors.Add(new FieldError(path, $"{dynamicTarget.Name} '{dynamicName}' does not exist."));
            }
            else if (field.Type == FieldType.Link && value is string linked)
            {
                if (!registry.TryGet(field.Options!, out var target))
                    errors.Add(new FieldError(path, $"Link target '{field.Options}' does not exist."));
                else if (!await repository.ExistsAsync(target, linked, ct))
                    errors.Add(new FieldError(path, $"{target.Name} '{linked}' does not exist."));
            }
        }
    }

    /// <summary>Validates the rows of one child table. <paramref name="current"/> is null for a brand new document.</summary>
    private async Task ValidateTableAsync(
        TableInput table,
        Dictionary<string, Dictionary<string, object?>>? current,
        List<FieldError> errors,
        IReadOnlyDictionary<string, object?> parentValues,
        IUserContext user,
        bool isNew,
        CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var multiLink = table.Field.MultiSelect ? DocTypeMetaExtensions.MultiSelectLink(table.Child) : null;
        var picked = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var prefix = $"{table.Field.Name}[{i}].";
            if (multiLink is not null && FieldValueConverter.Unwrap(row.Merged().GetValueOrDefault(multiLink.Name)) is string chosen && chosen.Length > 0 && !picked.Add(chosen))
                errors.Add(new FieldError(prefix + multiLink.Name, "This value is chosen twice."));

            if (row.Name is not null && current is not null)
            {
                if (!current.ContainsKey(row.Name))
                {
                    errors.Add(new FieldError(prefix + "name", "This row does not belong to the document."));
                    continue;
                }

                if (!seen.Add(row.Name))
                {
                    errors.Add(new FieldError(prefix + "name", "The same row appears twice."));
                    continue;
                }
            }

            var merged = row.Merged();
            var context = _logic.Context(table.Child, merged, [], parentValues, user, isNew);
            await ValidateAsync(table.Child, merged, row.Values.Keys, errors, prefix, context, null, ct);
        }
    }

    private static void CheckRequiredTables(
        DocTypeMeta meta, List<TableInput> tables, List<FieldError> errors, bool onlyIfProvided)
    {
        foreach (var field in TableFields(meta).Where(f => f.Required))
        {
            var provided = tables.FirstOrDefault(t => t.Field.Name == field.Name);
            if (provided is null ? !onlyIfProvided : provided.Rows.Count == 0)
                errors.Add(new FieldError(field.Name, "Add at least one row."));
        }
    }

    /// <summary>
    /// Runs the DocType's validation rules, and those of its child tables for each row. Rules read the finished
    /// document, so they only run once nothing more basic is wrong.
    /// </summary>
    private void ValidateRules(
        DocTypeMeta meta,
        List<TableInput> tables,
        IReadOnlyDictionary<string, object?> values,
        ExpressionContext context,
        IUserContext user,
        bool isNew,
        List<FieldError> errors)
    {
        if (errors.Count > 0)
            return;

        DocumentLogic.ValidateRules(meta, context, errors, string.Empty, string.Empty);
        foreach (var table in tables.Where(t => t.Provided && t.Child.Validations.Count > 0))
        {
            for (var i = 0; i < table.Rows.Count; i++)
            {
                var rowContext = _logic.Context(table.Child, table.Rows[i].Merged(), [], values, user, isNew);
                DocumentLogic.ValidateRules(table.Child, rowContext, errors, $"{table.Field.Name}[{i}].", $"{table.Field.Name}[{i}]");
            }
        }
    }

    /// <summary>Works out which rows to add, change and remove so the table matches what the client sent.</summary>
    private static ChildChanges BuildChanges(
        TableInput table,
        DocTypeMeta parent,
        string parentName,
        Dictionary<string, Dictionary<string, object?>> current,
        DateTime now,
        IUserContext user)
    {
        var inserts = new List<IReadOnlyDictionary<string, object?>>();
        var updates = new List<ChildUpdate>();
        var kept = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            var idx = i + 1;

            if (row.Name is not null && current.TryGetValue(row.Name, out var existing))
            {
                kept.Add(row.Name);
                var changed = row.Values
                    .Where(kv => !Equals(existing.GetValueOrDefault(kv.Key), kv.Value))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                if (Convert.ToInt32(existing["idx"]) != idx)
                    changed["idx"] = idx;

                if (changed.Count > 0)
                {
                    changed["modified"] = now;
                    changed["modified_by"] = user.User;
                    updates.Add(new ChildUpdate(row.Name, changed));
                }

                continue;
            }

            inserts.Add(new Dictionary<string, object?>(row.Values, StringComparer.Ordinal)
            {
                ["name"] = Guid.NewGuid().ToString("N")[..10],
                ["parent"] = parentName,
                ["parenttype"] = parent.Name,
                ["parentfield"] = table.Field.Name,
                ["idx"] = idx,
                ["creation"] = now,
                ["modified"] = now,
                ["modified_by"] = user.User,
                ["owner"] = user.User,
                ["docstatus"] = 0,
            });
        }

        var deletes = current.Keys.Where(name => !kept.Contains(name)).ToList();
        return new ChildChanges(table.Child, inserts, updates, deletes);
    }

    private DateTime Now()
    {
        // PostgreSQL stores microseconds; truncate so values round-trip exactly.
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}
