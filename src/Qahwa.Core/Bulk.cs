using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>What happened to one document in a bulk action.</summary>
public sealed record BulkResult(string Name, bool Ok, string? Error);

/// <summary>
/// Changes or deletes several documents chosen in a list. Each document goes through the ordinary engine on its own, so every
/// permission, rule, lock, log entry and notification applies exactly as if the person had done it one by one; a document that
/// cannot be changed is reported and the others still go ahead.
/// </summary>
public sealed class BulkService(IMetaRegistry registry, DocumentService documents)
{
    public const int MaxDocuments = 100;

    /// <summary>Not signed in, or no right at all to do this to the DocType: refuse once, instead of reporting every document as failed.</summary>
    private void RequireRight(string doctype, IUserContext user, bool delete)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();

        var rights = documents.GetPermissions(registry.Get(doctype), user);
        if (!(delete ? rights.Delete : rights.Write))
            throw new PermissionDeniedException($"You do not have {(delete ? "delete" : "write")} permission on {doctype}.");
    }

    private static IReadOnlyList<string> CheckNames(IReadOnlyList<string>? names)
    {
        var clean = (names ?? []).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).ToList();
        if (clean.Count == 0 || clean.Count > MaxDocuments)
            throw new DocumentValidationException([new FieldError("names", $"Choose 1 to {MaxDocuments} documents.")]);
        return clean;
    }

    private static string Describe(Exception ex) => ex switch
    {
        DocumentValidationException v => string.Join(" ", v.Errors.Select(e => e.Field.Length > 0 ? $"{e.Field}: {e.Message}" : e.Message)),
        QahwaException q => q.Message,
        _ => throw ex,
    };

    private async Task<IReadOnlyList<BulkResult>> RunAsync(IReadOnlyList<string> names, Func<string, Task> action)
    {
        var results = new List<BulkResult>();
        foreach (var name in names)
        {
            try
            {
                await action(name);
                results.Add(new BulkResult(name, true, null));
            }
            catch (QahwaException ex)
            {
                results.Add(new BulkResult(name, false, Describe(ex)));
            }
        }

        return results;
    }

    /// <summary>Sets one field to one value on each chosen document.</summary>
    public async Task<IReadOnlyList<BulkResult>> UpdateAsync(
        string doctype, IReadOnlyList<string>? names, string? field, object? value, IUserContext user, CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        RequireRight(doctype, user, delete: false);
        var clean = CheckNames(names);
        var target = meta.Fields.FirstOrDefault(f => f.Name == field);
        if (target is null || !FieldTypes.HasColumn(target.Type) || target.Type == FieldType.Password || target.Formula is not null)
            throw new DocumentValidationException([new FieldError("field", "Choose a field that can be set.")]);

        var change = new Dictionary<string, object?> { [target.Name] = value };
        return await RunAsync(clean, name => documents.UpdateAsync(doctype, name, change, user, ct));
    }

    public async Task<IReadOnlyList<BulkResult>> DeleteAsync(
        string doctype, IReadOnlyList<string>? names, IUserContext user, CancellationToken ct = default)
    {
        RequireRight(doctype, user, delete: true);
        var clean = CheckNames(names);
        return await RunAsync(clean, name => documents.DeleteAsync(doctype, name, user, ct));
    }
}
