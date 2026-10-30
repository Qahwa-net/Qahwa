using System.Security.Cryptography;
using System.Text;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Escalation: a document that has sat in a workflow state, untouched, for longer than the state allows is escalated by the
/// scheduler: the people of some roles are told, and/or the system does an action for it. It happens once per time in the
/// state (a marker row, named from the document and state, is claimed first, so two servers never escalate the same one).
/// The marker goes away when the document moves.
/// </summary>
public sealed class WorkflowEscalationService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    WorkflowCatalog catalog,
    NotificationService notifications,
    HistoryRecorder history)
{
    public const string DocTypeName = "Workflow Escalation";
    private const int MaxBatch = 100;
    private const int MaxRecipients = 100;
    private const string SystemUser = "system@qahwa";

    private static string MarkerName(string doctype, string name, string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{doctype}\n{name}\n{state}")))[..32].ToLowerInvariant();

    /// <summary>Forgets that a document was escalated (it has moved on, so a later wait starts afresh).</summary>
    internal static async Task ClearAsync(IMetaRegistry registry, IDocumentRepository repository, string doctype, string name, CancellationToken ct)
    {
        if (!registry.TryGet(DocTypeName, out var meta))
            return;
        var rows = await repository.ListAsync(meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            Fields = ["name"], OrderBy = "creation", Descending = false, Limit = 100,
        }, ct);
        foreach (var row in rows)
            await repository.DeleteAsync(meta, (string)row["name"]!, null, ct);
    }

    /// <summary>Escalates every document that is overdue. Called by the scheduler; safe on several servers at once.</summary>
    /// <returns>How many documents were escalated and how many escalations failed.</returns>
    public async Task<(int Escalated, int Failed)> RunDueAsync(CancellationToken ct = default)
    {
        if (!registry.TryGet(DocTypeName, out var markerMeta))
            return (0, 0);

        int escalated = 0, failed = 0;
        var now = history.Now();
        foreach (var workflow in catalog.All)
        {
            if (!registry.TryGet(workflow.DocType, out var meta))
                continue;
            foreach (var state in workflow.States.Where(s => s.EscalateAfterHours > 0))
            {
                var due = await repository.ListAsync(meta, new ListQuery
                {
                    Filters = [new(workflow.StateField, FilterOp.Eq, state.Name), new("modified", FilterOp.Le, now.AddHours(-state.EscalateAfterHours))],
                    Fields = ["name"], OrderBy = "modified", Descending = false, Limit = MaxBatch,
                }, ct);

                foreach (var row in due)
                {
                    var name = (string)row["name"]!;
                    var marker = MarkerName(meta.Name, name, state.Name);
                    if (await repository.ExistsAsync(markerMeta, marker, ct))
                        continue;
                    try
                    {
                        // Claim it first: a second server inserting the same marker gets a conflict and leaves it alone.
                        var claim = HistoryRecorder.Standard(SystemUser, now);
                        claim["name"] = marker;
                        claim["reference_doctype"] = meta.Name;
                        claim["reference_name"] = name;
                        claim["state"] = state.Name;
                        await repository.InsertAsync(markerMeta, claim, null, ct);
                    }
                    catch (ConflictException)
                    {
                        continue;
                    }

                    try
                    {
                        await EscalateAsync(workflow, state, meta, name, ct);
                        escalated++;
                    }
                    catch (Exception ex) when (ex is DocumentValidationException or PermissionDeniedException or NotFoundException or ConflictException)
                    {
                        failed++; // the marker stays, so a broken document is not retried every few minutes
                    }
                }
            }
        }

        return (escalated, failed);
    }

    private async Task EscalateAsync(WorkflowDefinition workflow, WorkflowState state, DocTypeMeta meta, string name, CancellationToken ct)
    {
        var system = new UserContext(SystemUser, [UserContext.AdministratorRole]);
        var text = $"Escalation: {meta.Name} {name} has been \"{state.Name}\" for {state.EscalateAfterHours} hour(s) with no change.";
        if (history is not null && registry.TryGet(HistoryRecorder.CommentDocType, out var commentMeta))
            await repository.InsertAsync(commentMeta, history.CommentRow(meta.Name, name, "Info", text, SystemUser, history.Now()), null, ct);

        if (state.EscalateNotify.Count > 0 && registry.TryGet("Has Role", out var hasRole))
        {
            var people = (await repository.ListAsync(hasRole, new ListQuery
            {
                Filters = [new("role", FilterOp.In, state.EscalateNotify.Cast<object?>().ToList())],
                Fields = ["user"], OrderBy = "name", Descending = false, Limit = MaxRecipients,
            }, ct)).Select(r => (string)r["user"]!).Distinct(StringComparer.Ordinal);
            foreach (var person in people)
                await notifications.NotifyAsync(person, "Alert", meta.Name, name, SystemUser, text, ct);
        }

        if (state.EscalateAction is { } action)
            await documents.ApplyWorkflowActionAsync(meta.Name, name, action, null, system, ct);
    }
}
