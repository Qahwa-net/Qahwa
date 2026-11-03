using System.Net;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// Network rules per role: a role with "allowed networks" can only be used from those addresses. Anyone who holds such a role is refused
/// (403) from anywhere else, on every request, whichever way they came (session or key). A person with several restricted roles must be
/// inside the networks of all of them. The rules are read from the Role documents and kept for a few seconds.
/// The roles that keep the system administrable (Administrator, System Manager) and the automatic role (All) can never be restricted,
/// so a wrong entry cannot lock everyone out; that is also why the site-wide list stays in configuration (ADR-042).
/// </summary>
public sealed class RoleNetworkService(IMetaRegistry registry, IDocumentRepository repository, TimeProvider clock)
{
    public const string FieldName = "allowed_networks";
    public static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(15);

    private static readonly string[] Protected = [UserContext.AdministratorRole, "System Manager", UserContext.AllRole];

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, IpAllowList> _rules = new Dictionary<string, IpAllowList>();
    private DateTime _loaded = DateTime.MinValue;

    /// <summary>The entries of a "allowed networks" text: one per line or separated by commas or spaces.</summary>
    public static IReadOnlyList<string> Entries(string? text) =>
        (text ?? string.Empty).Split(['\n', '\r', ',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Checks a Role document being saved: the entries must be real addresses or ranges, and protected roles cannot have any.</summary>
    public static void Validate(IReadOnlyDictionary<string, object?> role, List<FieldError> errors)
    {
        var entries = Entries(role.GetValueOrDefault(FieldName) as string);
        if (entries.Count == 0)
            return;
        if (role.GetValueOrDefault("role_name") is string name && Protected.Contains(name, StringComparer.Ordinal))
        {
            errors.Add(new FieldError(FieldName, $"'{name}' cannot be limited to networks: that could lock everyone out."));
            return;
        }

        if (entries.Count > 50)
            errors.Add(new FieldError(FieldName, "At most 50 addresses or ranges."));
        try
        {
            IpAllowList.Parse(entries);
        }
        catch (FormatException ex)
        {
            errors.Add(new FieldError(FieldName, ex.Message));
        }
    }

    private async Task<IReadOnlyDictionary<string, IpAllowList>> RulesAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            if (now - _loaded < CacheFor)
                return _rules;
        }

        var rules = new Dictionary<string, IpAllowList>(StringComparer.Ordinal);
        if (registry.TryGet("Role", out var meta))
        {
            var rows = await repository.ListAsync(meta, new ListQuery
            {
                Filters = [new(FieldName, FilterOp.IsNotNull)], Fields = ["name", FieldName], OrderBy = "name", Descending = false, Limit = 1000,
            }, ct);
            foreach (var row in rows)
            {
                var entries = Entries(row.GetValueOrDefault(FieldName) as string);
                if (entries.Count == 0 || Protected.Contains((string)row["name"]!, StringComparer.Ordinal))
                    continue;
                try
                {
                    rules[(string)row["name"]!] = IpAllowList.Parse(entries);
                }
                catch (FormatException)
                {
                    // a rule that cannot be read is skipped here (it is refused when saved): never take the system down for it
                }
            }
        }

        lock (_gate)
        {
            _rules = rules;
            _loaded = now;
        }

        return rules;
    }

    /// <summary>Forget the rules read so far (a Role was saved).</summary>
    public void Refresh()
    {
        lock (_gate)
            _loaded = DateTime.MinValue;
    }

    /// <summary>The role that keeps this person out of the address, or null when they may come in.</summary>
    public async Task<string?> BlockedByAsync(IUserContext user, IPAddress? address, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated || user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal))
            return null;
        var rules = await RulesAsync(ct);
        if (rules.Count == 0)
            return null;
        foreach (var role in user.Roles.Order(StringComparer.Ordinal))
        {
            if (rules.TryGetValue(role, out var allowed) && !allowed.Allows(address))
                return role;
        }

        return null;
    }
}
