using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

public sealed partial class DocumentService
{
    /// <summary>What a set Password field is shown as. It is never the secret, and sending it back leaves the secret unchanged.</summary>
    public const string PasswordMask = "********";

    /// <summary>Tells the people who have this document open that it changed (where it is, not what changed).</summary>
    private void Announce(DocTypeMeta meta, string name, IUserContext user, bool deleted) =>
        realtime?.Publish(new RealtimeEvent("doc", user.User, meta.Name, name, deleted));

    /// <summary>Encrypts the Password fields in <paramref name="values"/> just before they are stored; an empty one clears the secret.</summary>
    private void ProtectPasswords(DocTypeMeta meta, Dictionary<string, object?> values)
    {
        foreach (var field in meta.Fields.Where(f => f.Type == FieldType.Password))
        {
            if (!values.TryGetValue(field.Name, out var value))
                continue;
            if (value is not string { Length: > 0 } plain)
            {
                values[field.Name] = null;
                continue;
            }

            if (secrets is null)
                throw new InvalidOperationException("Password fields need a secret protector, and none is configured.");
            values[field.Name] = secrets.Protect(plain);
        }
    }

    /// <summary>A set password is shown as the mask and an empty one as nothing; the stored (encrypted) text never leaves the server.</summary>
    private static void MaskPasswords(DocTypeMeta meta, Dictionary<string, object?> doc)
    {
        foreach (var field in meta.Fields.Where(f => f.Type == FieldType.Password))
        {
            if (doc.TryGetValue(field.Name, out var value))
                doc[field.Name] = value is string { Length: > 0 } ? PasswordMask : null;
        }
    }

    /// <summary>
    /// The secret kept in a Password field, for code that must use it (an app talking to another system). It is not offered over the REST
    /// interface. Needs the right to change the document; returns null when nothing is set or the stored value cannot be decrypted.
    /// </summary>
    public async Task<string?> GetPasswordAsync(string doctype, string name, string field, IUserContext user, CancellationToken ct = default)
    {
        var meta = RequireTopLevel(registry.Get(doctype));
        var definition = meta.Fields.FirstOrDefault(f => f.Name == field && f.Type == FieldType.Password)
            ?? throw new DocumentValidationException([new FieldError(field, "This is not a Password field.")]);
        var (level, share) = await PreAuthorizeAsync(meta, user, PermAction.Write, name, ct);
        var doc = await LoadAsync(meta, name, ct) ?? throw new NotFoundException($"{meta.Name} '{name}' was not found.");
        Settle(level, share, PermAction.Write, doc, user);
        RequireRestrictions(await RestrictionsAsync(meta, user, ct), doc, meta);
        var access = permissions.FieldAccessFor(meta, user, doc.GetValueOrDefault("owner") as string, creating: false);
        if (!access.CanRead(definition) || !access.CanWrite(definition))
            throw new PermissionDeniedException("You do not have permission to use this field.");

        return doc.GetValueOrDefault(field) is string { Length: > 0 } stored ? secrets?.Unprotect(stored) : null;
    }
}
