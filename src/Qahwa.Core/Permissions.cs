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

public sealed class PermissionService
{
    public AccessLevel Evaluate(DocTypeMeta meta, IUserContext user, PermAction action)
    {
        if (user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal))
            return AccessLevel.All;

        var level = AccessLevel.None;
        foreach (var perm in meta.Permissions)
        {
            if (!user.Roles.Contains(perm.Role, StringComparer.Ordinal) || !Allows(perm, action))
                continue;

            // "Owner" restriction is meaningless for creating a document that has no owner yet.
            if (action == PermAction.Create || !perm.IfOwner)
                return AccessLevel.All;

            level = AccessLevel.Owner;
        }

        return level;
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
