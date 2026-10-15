using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Limits a user to some documents of a DocType: <paramref name="Field"/> ("name" or a Link field) must hold one of
/// <paramref name="Allowed"/>. Link fields may also be empty; the document's own name may not.
/// </summary>
public sealed record Restriction(string Field, IReadOnlySet<string> Allowed, bool AllowEmpty)
{
    public bool IsName => Field == "name";

    public bool Admits(object? value) =>
        value is string text && text.Length > 0 ? Allowed.Contains(text) : AllowEmpty;
}

/// <summary>
/// User Permissions: "this user may only work with Company X" restricts every DocType that links to Company, and
/// Company itself, to those values. Rows live in the User Permission DocType, so they are edited like any document.
/// </summary>
public sealed class UserPermissionService(IMetaRegistry registry, IDocumentRepository repository)
{
    public const string DocTypeName = "User Permission";
    private const int MaxRows = 1000;

    /// <summary>The restrictions that apply to this user on this DocType. Administrators have none.</summary>
    public async Task<IReadOnlyList<Restriction>> ForAsync(DocTypeMeta meta, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || user.Roles.Contains(UserContext.AdministratorRole) || meta.Name == DocTypeName
            || !registry.TryGet(DocTypeName, out var source))
        {
            return [];
        }

        var rows = await repository.ListAsync(source, new ListQuery
        {
            Filters = [new QueryFilter("user", FilterOp.Eq, user.User)],
            Fields = ["allow", "for_value", "apply_to_all_doctypes", "applicable_for"],
            OrderBy = "name",
            Descending = false,
            Limit = MaxRows,
        }, ct);

        var restrictions = new List<Restriction>();
        foreach (var group in rows
            .Where(r => AppliesTo(r, meta.Name))
            .GroupBy(r => (r["allow"] as string)?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Key.Length > 0))
        {
            var allowed = group.Select(r => r["for_value"] as string).Where(v => !string.IsNullOrEmpty(v))
                .ToHashSet(StringComparer.Ordinal)!;

            if (string.Equals(group.Key, meta.Name, StringComparison.OrdinalIgnoreCase))
                restrictions.Add(new Restriction("name", allowed!, AllowEmpty: false));
            foreach (var field in meta.Fields.Where(f =>
                f.Type == FieldType.Link && string.Equals(f.Options, group.Key, StringComparison.OrdinalIgnoreCase)))
            {
                restrictions.Add(new Restriction(field.Name, allowed!, AllowEmpty: true));
            }
        }

        return restrictions;
    }

    private static bool AppliesTo(Dictionary<string, object?> row, string doctype) =>
        IsTrue(row.GetValueOrDefault("apply_to_all_doctypes"))
        || string.Equals((row.GetValueOrDefault("applicable_for") as string)?.Trim(), doctype, StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        null => false,
        string s => s is "1" or "true",
        _ => Convert.ToInt64(value) != 0,
    };
}
