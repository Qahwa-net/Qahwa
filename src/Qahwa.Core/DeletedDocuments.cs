using System.Text.Json;
using System.Text.Json.Serialization;
using Qahwa.Abstractions;
using Qahwa.Core.Auth;

namespace Qahwa.Core;

/// <summary>
/// The safety net under deleting: when a document (a real one, not a setting, a child row or something the framework keeps) is deleted,
/// a copy of it and its table rows is kept in <c>Deleted Document</c> in the same transaction, so a System Manager can put it back. Old
/// copies are removed after the retention period. Password fields are not kept, and a document too large to keep is deleted as before.
/// </summary>
public sealed class DeletedDocumentService(IMetaRegistry registry, IDocumentRepository repository, PermissionService permissions, HistoryRecorder history, TimeProvider clock)
{
    public const string DocTypeName = "Deleted Document";

    /// <summary>The most a kept copy may hold (characters of JSON). A larger document is deleted without a copy.</summary>
    public const int MaxCopyLength = 1_000_000;

    public const int DefaultRetentionDays = 30;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static bool Keeps(DocTypeMeta meta) => AssignmentRuleManager.Applies(meta);

    /// <summary>The write that keeps a copy of <paramref name="document"/> (with its table rows) along with the delete, or null when none is kept.</summary>
    public static ChildChanges? Capture(IMetaRegistry registry, DocTypeMeta meta, IReadOnlyDictionary<string, object?> document, string user, DateTime now)
    {
        if (!Keeps(meta) || !registry.TryGet(DocTypeName, out var target) || document.GetValueOrDefault("name") is not string name)
            return null;

        var passwords = meta.Fields.Where(f => f.Type == FieldType.Password).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in document)
        {
            if (value is null || passwords.Contains(key))
                continue;
            copy[key] = value is IEnumerable<Dictionary<string, object?>> rows
                ? rows.Select(r => r.Where(c => c.Value is not null).ToDictionary(c => c.Key, c => c.Value)).ToList()
                : value;
        }

        var text = JsonSerializer.Serialize(copy, Json);
        if (text.Length > MaxCopyLength)
            return null;

        var row = HistoryRecorder.Standard(user, now);
        row["deleted_doctype"] = meta.Name;
        row["deleted_name"] = name;
        row["data"] = text;
        return new ChildChanges(target, [row], [], []);
    }

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private void RequireManager(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!permissions.IsAdministrator(user) && !user.Roles.Contains("System Manager", StringComparer.OrdinalIgnoreCase))
            throw new PermissionDeniedException("Only a System Manager can restore deleted documents.");
    }

    /// <summary>Puts a deleted document back, with its table rows, under the name it had. The kept copy is used up.</summary>
    /// <exception cref="ConflictException">A document with that name exists now (or a unique value is taken).</exception>
    public async Task<string> RestoreAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        RequireManager(user);
        var kept = await repository.GetAsync(Meta, id, ct) ?? throw new NotFoundException($"Deleted document '{id}' was not found.");
        var doctype = kept["deleted_doctype"] as string ?? throw new DocumentValidationException("This copy is damaged.");
        if (!registry.TryGet(doctype, out var meta) || !Keeps(meta))
            throw new DocumentValidationException($"{doctype} no longer exists, so its documents cannot be restored.");
        permissions.Require(meta, user, PermAction.Create);

        JsonElement data;
        try
        {
            using var parsed = JsonDocument.Parse((kept["data"] as string) ?? "{}");
            data = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new DocumentValidationException("This copy is damaged.");
        }

        if (data.ValueKind != JsonValueKind.Object)
            throw new DocumentValidationException("This copy is damaged.");

        var tables = meta.Fields.Where(f => f.Type == FieldType.Table && f.Options is not null).ToList();
        var top = data.EnumerateObject().Where(p => tables.All(t => t.Name != p.Name)).ToDictionary(p => p.Name, p => p.Value);
        Dictionary<string, object?> values;
        try
        {
            values = BackupService.RowFromJson(meta, JsonSerializer.Serialize(top));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new DocumentValidationException("This copy cannot be read: " + ex.Message);
        }

        var name = (string)values["name"]!;
        if (await repository.ExistsAsync(meta, name, ct))
            throw new ConflictException($"{meta.Name} '{name}' exists again. Rename or delete it first.");

        var children = new List<ChildChanges>();
        try
        {
            foreach (var field in tables)
            {
                if (!data.TryGetProperty(field.Name, out var rows) || rows.ValueKind != JsonValueKind.Array || !registry.TryGet(field.Options!, out var childMeta))
                    continue;
                var inserts = new List<IReadOnlyDictionary<string, object?>>();
                foreach (var row in rows.EnumerateArray().Take(DocumentService.MaxRows))
                {
                    if (row.ValueKind != JsonValueKind.Object)
                        throw new FormatException("A table row is not an object.");
                    var line = row.EnumerateObject().Where(p => p.Name is not ("parent" or "parenttype" or "parentfield")).ToDictionary(p => p.Name, p => (object)p.Value);
                    line["parent"] = name;
                    line["parenttype"] = meta.Name;
                    line["parentfield"] = field.Name;
                    inserts.Add(BackupService.RowFromJson(childMeta, JsonSerializer.Serialize(line)));
                }

                if (inserts.Count > 0)
                    children.Add(new ChildChanges(childMeta, inserts, [], []));
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            throw new DocumentValidationException("This copy cannot be read: " + ex.Message);
        }

        if (history.Version(meta, name, "Restore", null, user.User, clock.GetUtcNow().UtcDateTime) is { } logged)
            children.Add(logged);

        await repository.InsertAsync(meta, values, children, ct);
        await repository.DeleteAsync(Meta, id, null, ct);
        return name;
    }

    /// <summary>Removes the copies older than <paramref name="retention"/>. Returns how many.</summary>
    public async Task<int> PurgeAsync(TimeSpan retention, CancellationToken ct = default)
    {
        var cutoff = clock.GetUtcNow().UtcDateTime - retention;
        var removed = 0;
        while (true)
        {
            var old = await repository.ListAsync(Meta, new ListQuery
            {
                Filters = [new("creation", FilterOp.Lt, cutoff)],
                Fields = ["name"], Limit = 200,
            }, ct);
            if (old.Count == 0)
                break;
            foreach (var row in old)
            {
                await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
                removed++;
            }
        }

        return removed;
    }
}
