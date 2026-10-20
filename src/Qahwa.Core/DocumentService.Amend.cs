using Qahwa.Abstractions;

namespace Qahwa.Core;

public sealed partial class DocumentService
{
    /// <summary>The link a submittable DocType gets: the cancelled document this one replaces.</summary>
    public const string AmendedFromField = DocTypeMetaExtensions.AmendedFromField;

    /// <summary>
    /// Amends a cancelled document (ADR-103): makes a new draft with the same values, linked back through <c>amended_from</c>. A document
    /// is amended once (the new one can be cancelled and amended again). It needs read access to the cancelled document, the Amend right and the right to create.
    /// </summary>
    public async Task<Dictionary<string, object?>> AmendAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        var meta = registry.Get(doctype);
        if (!meta.IsSubmittable || meta.IsChildTable)
            throw General($"{meta.Name} is not a submittable DocType.");
        var source = await GetAsync(doctype, name, user, ct);
        if (user.IsAuthenticated)
            permissions.Require(meta, user, PermFlag.Amend);
        if (Convert.ToInt32(source.GetValueOrDefault("docstatus") ?? 0) != 2)
            throw General("Only a cancelled document can be amended.");
        var already = await repository.ListAsync(meta, new ListQuery { Filters = [new(AmendedFromField, FilterOp.Eq, name)], Fields = ["name"], Limit = 1 }, ct);
        if (already.Count > 0)
            throw new ConflictException($"{meta.Name} '{name}' was already amended as '{already[0]["name"]}'.");

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var field in meta.Fields.Where(f => IsCopied(f)))
        {
            if (field.Type == FieldType.Table)
            {
                if (source.GetValueOrDefault(field.Name) is IEnumerable<object?> rows && registry.TryGet(field.Options ?? string.Empty, out var child))
                {
                    values[field.Name] = rows.OfType<IReadOnlyDictionary<string, object?>>()
                        .Select(row => child.Fields.Where(f => f.Type != FieldType.Table && IsCopied(f) && row.ContainsKey(f.Name))
                            .ToDictionary(f => f.Name, f => row[f.Name], StringComparer.Ordinal))
                        .ToList();
                }
            }
            else if (source.TryGetValue(field.Name, out var value) && value is not null)
                values[field.Name] = value;
        }

        var created = await InsertAsync(doctype, values, user, ct);
        // The link is set here, not accepted from a client, so nobody can claim another document was amended.
        await repository.UpdateAsync(meta, (string)created["name"]!, new Dictionary<string, object?> { [AmendedFromField] = name }, null, null, ct);
        created[AmendedFromField] = name;
        return created;
    }

    private static bool IsCopied(DocFieldMeta f) =>
        !FieldTypes.IsLayout(f.Type) && f.Type != FieldType.Password && f.Formula is null && !f.Unique && !f.NoCopy && f.Name != AmendedFromField;
}
