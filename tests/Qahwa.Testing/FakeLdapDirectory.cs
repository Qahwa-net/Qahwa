using Qahwa.Core.Auth;

namespace Qahwa.Testing;

/// <summary>A stand-in company directory: people with a distinguished name, email, name and password.</summary>
public sealed class FakeLdapDirectory : ILdapDirectory
{
    private readonly List<(string Dn, string Email, string? Name, string Password)> _people = [];

    public List<string> Filters { get; } = [];

    public List<(string Dn, string Password)> Binds { get; } = [];

    public int Searches => Filters.Count;

    public bool Down { get; set; }

    public FakeLdapDirectory Add(string dn, string email, string password, string? name = null)
    {
        _people.Add((dn, email, name, password));
        return this;
    }

    public void ChangeEmail(string dn, string email)
    {
        var at = _people.FindIndex(p => p.Dn == dn);
        _people[at] = _people[at] with { Email = email };
    }

    public Task<IReadOnlyList<LdapEntry>> SearchAsync(LdapOptions options, string filter, CancellationToken ct = default)
    {
        if (Down)
            throw new IOException("The directory is not reachable.");
        Filters.Add(filter);
        // Only the plain shape the default filter makes is understood: (mail=<escaped value>) at the end.
        var start = filter.IndexOf("(mail=", StringComparison.Ordinal);
        if (start < 0 || !filter.EndsWith(')'))
            return Task.FromResult<IReadOnlyList<LdapEntry>>([]);
        var raw = filter[(start + 6)..^2];
        var value = raw.Replace("\\2a", "*").Replace("\\28", "(").Replace("\\29", ")").Replace("\\5c", "\\").Replace("\\00", "\0");
        var matches = _people.Where(p => string.Equals(p.Email, value, StringComparison.OrdinalIgnoreCase)).Take(2).Select(p => new LdapEntry(p.Dn, p.Email, p.Name)).ToList();
        return Task.FromResult<IReadOnlyList<LdapEntry>>(matches);
    }

    public Task<bool> BindAsync(LdapOptions options, string dn, string password, CancellationToken ct = default)
    {
        Binds.Add((dn, password));
        if (Down)
            throw new IOException("The directory is not reachable.");
        return Task.FromResult(password.Length > 0 && _people.Any(p => p.Dn == dn && p.Password == password));
    }
}
