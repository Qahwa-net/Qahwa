namespace Qahwa.Core;

/// <summary>A naming series: the prefix, how many digits the number has, the last number used, and which DocTypes use it.</summary>
public sealed record SeriesInfo(string Prefix, int Digits, long Current, IReadOnlyList<string> DocTypes);

/// <summary>Shows and adjusts the counters behind series names such as <c>CUST-.#####</c>. Only for System Managers.</summary>
public sealed class NamingSeriesManager(IMetaRegistry registry, IDocumentRepository repository, PermissionService permissions)
{
    public const long MaxCounter = 999_999_999_999;

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!permissions.IsAdministrator(user) && !user.Roles.Contains("System Manager", StringComparer.Ordinal))
            throw new PermissionDeniedException("Only a System Manager can manage naming series.");
    }

    private static bool IsSeries(DocTypeMeta meta) =>
        meta.Autoname is { } rule && rule is not ("hash" or "prompt") && !rule.StartsWith("field:", StringComparison.Ordinal)
        && !rule.StartsWith(NamingService.ExpressionPrefix, StringComparison.Ordinal);

    public async Task<IReadOnlyList<SeriesInfo>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var stored = (await repository.ListSeriesAsync(ct)).ToDictionary(c => c.Prefix, c => c.Current, StringComparer.Ordinal);
        var used = registry.All.Where(m => !m.IsChildTable && IsSeries(m))
            .GroupBy(m => NamingService.ParseSeries(m.Autoname!), m => m.Name)
            .ToDictionary(g => g.Key, g => g.Order(StringComparer.Ordinal).ToList());

        // A pattern with {YYYY} or {field} has no counter of its own: its counters are the prefixes that came out of it.
        var plain = used.Where(kv => !kv.Key.Prefix.Contains('{')).ToDictionary(kv => kv.Key, kv => kv.Value);
        var patterns = used.Where(kv => kv.Key.Prefix.Contains('{')).Select(kv => (Regex: new System.Text.RegularExpressions.Regex(NamingService.PrefixRegex(kv.Key.Prefix), System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50)), kv.Key.Digits, kv.Value)).ToList();

        var prefixes = plain.Keys.Select(k => k.Prefix).Concat(stored.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        return prefixes.Select(prefix =>
        {
            var match = plain.FirstOrDefault(kv => kv.Key.Prefix == prefix);
            if (match.Value is not null)
                return new SeriesInfo(prefix, match.Key.Digits, stored.GetValueOrDefault(prefix), match.Value);
            var viaPattern = patterns.FirstOrDefault(p => p.Regex.IsMatch(prefix));
            return new SeriesInfo(prefix, viaPattern.Value is null ? 0 : viaPattern.Digits, stored.GetValueOrDefault(prefix), viaPattern.Value ?? []);
        }).ToList();
    }

    /// <summary>
    /// Sets the last number used. The next document gets <paramref name="current"/> + 1. Only prefixes a DocType uses (or that
    /// already have a counter) can be set, so the table cannot be filled with arbitrary names.
    /// </summary>
    public async Task SetAsync(string prefix, long current, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        if (current is < 0 or > MaxCounter)
            throw new DocumentValidationException([new FieldError("current", $"Use a number from 0 to {MaxCounter}.")]);

        var known = (await ListAsync(user, ct)).Any(s => string.Equals(s.Prefix, prefix, StringComparison.Ordinal));
        if (!known)
            throw new NotFoundException($"No DocType uses the series '{prefix}'.");

        await repository.SetSeriesAsync(prefix, current, ct);
    }
}
