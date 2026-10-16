using System.Text.Json;
using System.Text.RegularExpressions;

namespace Qahwa.Hosting;

/// <summary>One site: its own database and settings, reached by its own host names.</summary>
/// <param name="Name">Letters, digits and dashes; the folder name under the sites directory.</param>
/// <param name="Hosts">Host names (acme.example.com) or one-level wildcards (*.example.com), lower case, no port.</param>
/// <param name="ConnectionString">The database of this site.</param>
/// <param name="ConfigPath">The <c>site_config.json</c> file; all of it is layered over the shared configuration for this site.</param>
public sealed record SiteDefinition(string Name, IReadOnlyList<string> Hosts, string ConnectionString, string ConfigPath);

public sealed class SiteConfigException(IReadOnlyList<string> problems)
    : Exception("The sites are not set up correctly:\n- " + string.Join("\n- ", problems))
{
    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>
/// Reads the sites from a directory: <c>&lt;sites&gt;/&lt;name&gt;/site_config.json</c>, with <c>hosts</c> and either
/// <c>connectionString</c> or <c>connectionStringEnv</c> (the name of an environment variable, so the secret stays out of the file).
/// Everything is checked together and a mistake stops the server at start-up: a site that quietly goes missing, or two sites that
/// share a database or a host name, must never reach people's data.
/// </summary>
public static partial class SiteCatalog
{
    public const string ConfigFile = "site_config.json";

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NameRule();

    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelRule();

    public static bool ValidName(string? name) => name is not null && NameRule().IsMatch(name);

    /// <summary>
    /// The host name as used for matching: lower case, no port, no trailing dot; null when it is not a plausible host name.
    /// A pattern may start with <c>*.</c> only when <paramref name="allowWildcard"/> is set (in the configuration, never in a request).
    /// </summary>
    public static string? NormalizeHost(string? raw, bool allowWildcard = false)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 300)
            return null;
        var host = raw.Trim().ToLowerInvariant();
        if (host.StartsWith('['))
        {
            var end = host.IndexOf(']', StringComparison.Ordinal);
            if (end < 0 || (host.Length > end + 1 && !PortRule(host[(end + 1)..])) || !System.Net.IPAddress.TryParse(host[1..end], out _))
                return null;
            return host[..(end + 1)];
        }

        var colon = host.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!PortRule(host[colon..]))
                return null;
            host = host[..colon];
        }

        host = host.TrimEnd('.');
        if (host.Length is 0 or > 253)
            return null;
        var labels = host.Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            if (allowWildcard && i == 0 && labels[i] == "*" && labels.Length > 2)
                continue;
            if (!LabelRule().IsMatch(labels[i]))
                return null;
        }

        return host;
    }

    private static bool PortRule(string text) => text.Length is > 1 and <= 6 && text[0] == ':' && text[1..].All(char.IsAsciiDigit);

    /// <summary>Reads every site under <paramref name="sitesPath"/>; an empty or missing directory means no sites.</summary>
    /// <exception cref="SiteConfigException">Something is wrong with any of them.</exception>
    public static IReadOnlyList<SiteDefinition> Load(string sitesPath, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var problems = new List<string>();
        var sites = new List<SiteDefinition>();
        if (Directory.Exists(sitesPath))
        {
            foreach (var directory in Directory.GetDirectories(sitesPath).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(directory);
                var file = Path.Combine(directory, ConfigFile);
                if (!File.Exists(file))
                    continue; // another kind of folder
                if (!ValidName(name))
                {
                    problems.Add($"'{name}' is not a valid site name (lower-case letters, digits and dashes, up to 40).");
                    continue;
                }

                try
                {
                    sites.Add(Read(name, file, environment, problems));
                }
                catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
                {
                    problems.Add($"Site '{name}': {file} could not be read ({e.Message}).");
                }
            }
        }

        problems.AddRange(Check(sites));
        if (problems.Count > 0)
            throw new SiteConfigException(problems);
        return sites;
    }

    private static SiteDefinition Read(string name, string file, Func<string, string?> environment, List<string> problems)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("it must be a JSON object");

        var hosts = new List<string>();
        if (root.TryGetProperty("hosts", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var host = item.ValueKind == JsonValueKind.String ? NormalizeHost(item.GetString(), allowWildcard: true) : null;
                if (host is null)
                    problems.Add($"Site '{name}': '{item}' is not a valid host name.");
                else
                    hosts.Add(host);
            }
        }

        string? Text(string property) => root.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var connection = Text("connectionString");
        if (string.IsNullOrWhiteSpace(connection) && Text("connectionStringEnv") is { Length: > 0 } variable)
        {
            connection = environment(variable);
            if (string.IsNullOrWhiteSpace(connection))
                problems.Add($"Site '{name}': the environment variable {variable} is not set.");
        }

        if (string.IsNullOrWhiteSpace(connection) && !problems.Any(p => p.StartsWith($"Site '{name}': the environment", StringComparison.Ordinal)))
            problems.Add($"Site '{name}': connectionString (or connectionStringEnv) is required.");
        return new SiteDefinition(name, hosts, connection ?? string.Empty, file);
    }

    /// <summary>Problems between sites: no host names, the same host name twice, the same database twice.</summary>
    public static IEnumerable<string> Check(IReadOnlyList<SiteDefinition> sites)
    {
        foreach (var site in sites.Where(s => s.Hosts.Count == 0))
            yield return $"Site '{site.Name}' has no hosts.";
        foreach (var group in sites.SelectMany(s => s.Hosts.Select(h => (Host: h, s.Name))).GroupBy(x => x.Host).Where(g => g.Select(x => x.Name).Distinct().Count() > 1))
            yield return $"The host '{group.Key}' belongs to more than one site ({string.Join(", ", group.Select(x => x.Name).Distinct())}).";
        foreach (var group in sites.Where(s => s.ConnectionString.Length > 0).GroupBy(s => s.ConnectionString.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            yield return $"Sites {string.Join(", ", group.Select(s => $"'{s.Name}'"))} share one database; each site needs its own.";
    }
}

/// <summary>Finds the site for the Host header of a request.</summary>
public sealed class SiteResolver
{
    private readonly Dictionary<string, SiteDefinition> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SiteDefinition> _wildcards = new(StringComparer.Ordinal);

    public SiteResolver(IEnumerable<SiteDefinition> sites)
    {
        foreach (var site in sites)
        {
            foreach (var host in site.Hosts)
            {
                if (host.StartsWith("*.", StringComparison.Ordinal))
                    _wildcards[host[2..]] = site;
                else
                    _exact[host] = site;
            }
        }
    }

    public bool IsEmpty => _exact.Count == 0 && _wildcards.Count == 0;

    /// <returns>The site, or null for an unknown or malformed host. An exact name wins over a wildcard; a wildcard covers one label only.</returns>
    public SiteDefinition? Resolve(string? hostHeader)
    {
        var host = SiteCatalog.NormalizeHost(hostHeader);
        if (host is null)
            return null;
        if (_exact.TryGetValue(host, out var site))
            return site;
        var dot = host.IndexOf('.', StringComparison.Ordinal);
        return dot > 0 && _wildcards.TryGetValue(host[(dot + 1)..], out var wild) ? wild : null;
    }
}
