using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>
/// Adds the field that holds a document's workflow state, for people building a workflow (Frappe's "workflow_state" field).
/// On a DocType made in the designer it becomes a field of that DocType; on a framework or app DocType it becomes a custom
/// field (Customize Form), so upgrades never touch it. Nothing is dropped and an existing field is reused.
/// </summary>
public sealed class WorkflowStateFieldService(IMetaRegistry registry, DocTypeDesigner designer, CustomizationManager customization)
{
    public const string FieldName = "workflow_state";

    /// <returns>The name of the state field: the existing one when the DocType already has a field of that name, otherwise the new one.</returns>
    public async Task<string> EnsureAsync(string doctype, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!registry.TryGet(doctype, out var meta) || meta.IsChildTable || meta.IsSingle)
            throw new NotFoundException($"DocType '{doctype}' was not found.");

        var source = registry.SourceOf(doctype) ?? DocTypeSource.App;
        var name = source == DocTypeSource.Custom ? FieldName : Customization.CustomPrefix + FieldName;

        if (meta.Fields.FirstOrDefault(f => f.Name == name) is { } existing)
        {
            if (WorkflowManager.StateFieldsOf(meta).Contains(existing.Name))
                return name;
            throw new ConflictException($"'{doctype}' already has a field named '{name}' that cannot hold a state. Rename or remove it first.");
        }

        var field = new DocFieldMeta
        {
            Name = name,
            Type = FieldType.Data,
            Label = "Workflow State",
            Description = "Set by the workflow; it cannot be edited by hand.",
            Hidden = true,
            ReadOnly = true,
            NoCopy = true,
            AllowOnSubmit = meta.IsSubmittable, // the workflow moves the state after submit; only submittable DocTypes accept the flag
            InListView = true,
        };

        if (source == DocTypeSource.Custom)
        {
            var detail = await designer.GetAsync(doctype, user, ct);
            await designer.UpdateAsync(doctype, meta with { Fields = [.. meta.Fields, field] }, detail.Modified, user, ct);
        }
        else
        {
            var view = await customization.GetAsync(doctype, user, ct);
            var changed = view.Customization with { CustomFields = [.. view.Customization.CustomFields, field] };
            await customization.SaveAsync(doctype, changed, view.Modified, user, ct);
        }
        return name;
    }
}
