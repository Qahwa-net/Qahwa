using System.Collections.Immutable;

namespace Qahwa.Core;

public interface IUserContext
{
    string User { get; }
    IReadOnlyCollection<string> Roles { get; }
    bool IsAuthenticated => User != UserContext.Guest;
}

public sealed record UserContext(string User, IReadOnlyCollection<string> Roles) : IUserContext
{
    public const string Guest = "Guest";
    public const string AdministratorRole = "Administrator";

    public static UserContext Anonymous { get; } = new(Guest, []);
}

public enum PermAction
{
    Read,
    Write,
    Create,
    Delete,
}

public enum AccessLevel
{
    None,

    /// <summary>Allowed, but only for documents the user owns.</summary>
    Owner,

    All,
}

public sealed record EffectivePermissions(bool Read, bool Write, bool Create, bool Delete);

/// <summary>A DocType as one person may see it (see <see cref="DocumentService.GetMetaView"/>).</summary>
public sealed record MetaView(
    DocTypeMeta Meta,
    EffectivePermissions Permissions,
    IReadOnlyDictionary<string, DocTypeMeta> Children,
    IReadOnlyList<int> WritableLevels);

/// <summary>Permission rules set in the Role Permission Manager. They replace the rules in a DocType's definition.</summary>
public interface IPermissionOverrides
{
    /// <returns>The customised rules, or null when the DocType uses the rules of its definition.</returns>
    IReadOnlyList<DocPermMeta>? Get(string docType);
}

/// <summary>Thread-safe in-memory copy of the customised rules; readers always see a consistent snapshot.</summary>
public sealed class PermissionOverrides : IPermissionOverrides
{
    private ImmutableDictionary<string, IReadOnlyList<DocPermMeta>> _rules =
        ImmutableDictionary.Create<string, IReadOnlyList<DocPermMeta>>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DocPermMeta>? Get(string docType) => _rules.GetValueOrDefault(docType);

    public void Set(string docType, IReadOnlyList<DocPermMeta> rules) =>
        ImmutableInterlocked.AddOrUpdate(ref _rules, docType, rules, (_, _) => rules);

    public void Remove(string docType) => ImmutableInterlocked.TryRemove(ref _rules, docType, out _);
}

/// <summary>
/// Which permission levels of a DocType's fields a person may read and write for one document.
/// Level 0 is the ordinary access to the document; levels 1 to 9 need a permission rule of their own.
/// </summary>
public sealed class FieldAccess(IReadOnlySet<int> readable, IReadOnlySet<int> writable)
{
    public static FieldAccess Everything { get; } = new(
        Enumerable.Range(0, 10).ToHashSet(), Enumerable.Range(0, 10).ToHashSet());

    public IReadOnlySet<int> Readable { get; } = readable;

    public IReadOnlySet<int> Writable { get; } = writable;

    public bool CanRead(DocFieldMeta field) => Readable.Contains(field.PermLevel);

    public bool CanWrite(DocFieldMeta field) => Writable.Contains(field.PermLevel);

    /// <summary>True when every field is readable and writable (the common case: nothing to enforce).</summary>
    public bool IsUnrestricted => Readable.Count == 10 && Writable.Count == 10;
}

public sealed class PermissionService(IPermissionOverrides? overrides = null)
{
    /// <summary>The rules in force for a DocType: customised ones if there are any, else those of its definition.</summary>
    public IReadOnlyList<DocPermMeta> RulesFor(DocTypeMeta meta) => overrides?.Get(meta.Name) ?? meta.Permissions;

    public bool IsAdministrator(IUserContext user) =>
        user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal);

    public AccessLevel Evaluate(DocTypeMeta meta, IUserContext user, PermAction action)
    {
        if (IsAdministrator(user))
            return AccessLevel.All;

        var level = AccessLevel.None;
        foreach (var perm in RulesFor(meta))
        {
            if (perm.PermLevel != 0 || !user.Roles.Contains(perm.Role, StringComparer.Ordinal) || !Allows(perm, action))
                continue;

            // "Owner" restriction is meaningless for creating a document that has no owner yet.
            if (action == PermAction.Create || !perm.IfOwner)
                return AccessLevel.All;

            level = AccessLevel.Owner;
        }

        return level;
    }

    /// <summary>
    /// The field levels the user may read and write on one document.
    /// <paramref name="documentOwner"/> is null when it is not known (lists): rules limited to own documents then grant nothing above level 0.
    /// </summary>
    public FieldAccess FieldAccessFor(DocTypeMeta meta, IUserContext user, string? documentOwner, bool creating)
    {
        if (IsAdministrator(user))
            return FieldAccess.Everything;

        var owns = creating || string.Equals(documentOwner, user.User, StringComparison.Ordinal);
        var readable = new HashSet<int> { 0 }; // reaching a document at all means level 0 is readable
        var writable = new HashSet<int>();
        if (Evaluate(meta, user, creating ? PermAction.Create : PermAction.Write) != AccessLevel.None)
            writable.Add(0);

        foreach (var rule in RulesFor(meta).Where(r => r.PermLevel > 0 && user.Roles.Contains(r.Role, StringComparer.Ordinal)))
        {
            if (rule.IfOwner && !owns)
                continue;
            if (rule.Read || rule.Write)
                readable.Add(rule.PermLevel);
            if (rule.Write)
                writable.Add(rule.PermLevel);
        }

        return new FieldAccess(readable, writable);
    }

    public AccessLevel Require(DocTypeMeta meta, IUserContext user, PermAction action)
    {
        var level = Evaluate(meta, user, action);
        if (level != AccessLevel.None)
            return level;

        throw user.IsAuthenticated
            ? new PermissionDeniedException($"You do not have {action.ToString().ToLowerInvariant()} permission on {meta.Name}.")
            : new UnauthenticatedException();
    }

    public void RequireOwnership(AccessLevel level, IReadOnlyDictionary<string, object?> document, IUserContext user)
    {
        if (level == AccessLevel.Owner
            && !string.Equals(document.GetValueOrDefault("owner") as string, user.User, StringComparison.Ordinal))
        {
            throw new PermissionDeniedException("You can only access documents you own.");
        }
    }

    private static bool Allows(DocPermMeta perm, PermAction action) => action switch
    {
        PermAction.Read => perm.Read,
        PermAction.Write => perm.Write,
        PermAction.Create => perm.Create,
        PermAction.Delete => perm.Delete,
        _ => false,
    };
}
