using Qahwa.Abstractions;

namespace Qahwa.Core.Designer;

public sealed record DesignerItem(
    string Name, string? Module, DocTypeSource Source, bool Editable, int FieldCount, bool IsChildTable);

public sealed record DesignerDetail(
    DocTypeMeta Definition,
    DocTypeSource Source,
    bool Editable,
    DateTime? Modified,
    long? DocumentCount);

/// <summary>What saving a definition would do, without doing it.</summary>
public sealed record DesignerPreview(
    IReadOnlyList<FieldError> Errors,
    SchemaPreview? Schema,
    IReadOnlyList<string> Notes);

/// <summary>
/// Creates, changes and deletes DocTypes at runtime. Definitions are validated, the table is changed with the
/// same additive-only rules as migrations, the definition is stored, and the live registry is swapped, so the
/// new DocType works immediately without a restart.
/// </summary>
public sealed class DocTypeDesigner(
    IMutableMetaRegistry registry,
    IDocTypeStore store,
    ISchemaManager schema,
    IDocumentRepository repository,
    TimeProvider clock)
{
    private const string SystemManagerRole = "System Manager";

    /// <summary>Names kept free for framework DocTypes that are planned, so upgrades never collide with user data.</summary>
    private static readonly HashSet<string> FrameworkNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "DocType", "DocField", "DocPerm", "Custom Field", "Property Setter", "Custom DocPerm", "Comment", "Communication",
        "ToDo", "DocShare", "Document Follow", "Version", "File", "Tag", "Note", "Workflow", "Workflow Action", "Notification",
        "Notification Log", "Email Template", "Email Account", "Assignment Rule", "Auto Repeat", "Webhook", "Report",
        "Dashboard", "Dashboard Chart", "Number Card", "Workspace", "Print Format", "Letter Head", "Web Form", "Data Import",
        "Scheduled Job Type", "System Settings", "Module Def", "Series", "Session", "Error Log", "Activity Log", "Access Log",
        "View Log", "Deleted Document", "List Filter", "User Settings",
    };

    public async Task<IReadOnlyList<DesignerItem>> ListAsync(IUserContext user, CancellationToken ct = default)
    {
        RequireDesigner(user);
        await Task.CompletedTask;
        return registry.All
            .OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Select(m =>
            {
                var source = registry.SourceOf(m.Name) ?? DocTypeSource.App;
                return new DesignerItem(m.Name, m.Module, source, source == DocTypeSource.Custom, m.Fields.Count, m.IsChildTable);
            })
            .ToList();
    }

    public async Task<DesignerDetail> GetAsync(string name, IUserContext user, CancellationToken ct = default)
    {
        RequireDesigner(user);
        var meta = registry.Get(name);
        var source = registry.SourceOf(name) ?? DocTypeSource.App;
        if (source != DocTypeSource.Custom)
            return new DesignerDetail(meta, source, false, null, null);

        var stored = await store.GetAsync(name, ct);
        var count = await repository.CountAsync(meta, new ListQuery(), ct);
        return new DesignerDetail(meta, source, true, stored?.Modified, count);
    }

    public async Task<DesignerPreview> PreviewAsync(
        DocTypeMeta definition, string? existingName, IUserContext user, CancellationToken ct = default)
    {
        RequireDesigner(user);

        DocTypeMeta? existing = null;
        if (existingName is not null)
        {
            if (registry.SourceOf(existingName) != DocTypeSource.Custom || !registry.TryGet(existingName, out existing))
                throw new NotFoundException($"Custom DocType '{existingName}' was not found.");
        }

        var errors = Validate(definition, existing).ToList();
        if (errors.Count > 0)
            return new DesignerPreview(errors, null, []);

        var preview = await schema.PreviewAsync(definition, ct);
        errors.AddRange(preview.Warnings.Select(w => new FieldError(string.Empty, w)));
        return new DesignerPreview(errors, preview, Notes(definition, existing));
    }

    // Changes to the schema are applied one at a time within this process; another instance racing us is caught
    // by the database (duplicate table, optimistic concurrency on the stored definition).
    private readonly SemaphoreSlim _changes = new(1, 1);

    private async Task<T> SerializedAsync<T>(Func<Task<T>> change, CancellationToken ct)
    {
        await _changes.WaitAsync(ct);
        try
        {
            return await change();
        }
        finally
        {
            _changes.Release();
        }
    }

    public Task<DesignerDetail> CreateAsync(DocTypeMeta definition, IUserContext user, CancellationToken ct = default) =>
        SerializedAsync(() => CreateCoreAsync(definition, user, ct), ct);

    public Task<DesignerDetail> UpdateAsync(
        string name, DocTypeMeta definition, DateTime? expectedModified, IUserContext user, CancellationToken ct = default) =>
        SerializedAsync(() => UpdateCoreAsync(name, definition, expectedModified, user, ct), ct);

    public Task DeleteAsync(string name, IUserContext user, CancellationToken ct = default) =>
        SerializedAsync(async () => { await DeleteCoreAsync(name, user, ct); return true; }, ct);

    private async Task<DesignerDetail> CreateCoreAsync(DocTypeMeta definition, IUserContext user, CancellationToken ct)
    {
        RequireDesigner(user);
        ThrowIfInvalid(Validate(definition, null));

        var preview = await schema.PreviewAsync(definition, ct);
        if (preview.TableExists)
        {
            // Left over from an earlier attempt that failed half way: reuse it only if it holds nothing.
            if (await repository.CountAsync(definition, new ListQuery(), ct) > 0)
                throw new ConflictException($"A table for '{definition.Name}' already exists and contains data.");
            ThrowIfInvalid(preview.Warnings.Select(w => new FieldError(string.Empty, w)).ToList());
        }

        await schema.ApplyAsync(definition, ct);

        var now = Now();
        await store.InsertAsync(
            new StoredDocType(definition.Name, DocTypeLoader.Serialize(definition), now, now, user.User, user.User), ct);
        registry.Upsert(definition, DocTypeSource.Custom);

        return await GetAsync(definition.Name, user, ct);
    }

    private async Task<DesignerDetail> UpdateCoreAsync(
        string name, DocTypeMeta definition, DateTime? expectedModified, IUserContext user, CancellationToken ct)
    {
        RequireDesigner(user);
        RequireCustom(name);

        var stored = await store.GetAsync(name, ct) ?? throw new NotFoundException($"DocType '{name}' was not found.");
        var existing = registry.Get(name);

        var errors = Validate(definition, existing).ToList();
        if (!string.Equals(definition.Name, name, StringComparison.Ordinal))
            errors.Add(new FieldError("name", "A DocType cannot be renamed yet."));
        ThrowIfInvalid(errors);

        if (expectedModified is not null && expectedModified != stored.Modified)
            throw new ConflictException($"'{name}' was changed by someone else after you opened it. Reload and try again.");

        var preview = await schema.PreviewAsync(definition, ct);
        ThrowIfInvalid(preview.Warnings.Select(w => new FieldError(string.Empty, w)).ToList());
        await schema.ApplyAsync(definition, ct);

        var updated = stored with
        {
            Definition = DocTypeLoader.Serialize(definition),
            Modified = Now(),
            ModifiedBy = user.User,
        };
        if (!await store.UpdateAsync(updated, stored.Modified, ct))
            throw new ConflictException($"'{name}' was changed or deleted by someone else. Reload and try again.");

        registry.Upsert(definition, DocTypeSource.Custom);
        return await GetAsync(name, user, ct);
    }

    private async Task DeleteCoreAsync(string name, IUserContext user, CancellationToken ct)
    {
        RequireDesigner(user);
        RequireCustom(name);

        var meta = registry.Get(name);
        var referencing = registry.All
            .Where(m => !string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(m => m.Fields
                .Where(f => f.Type is FieldType.Link or FieldType.Table
                            && string.Equals(f.Options, name, StringComparison.OrdinalIgnoreCase))
                .Select(f => $"{m.Name}.{f.Name}"))
            .ToList();
        if (referencing.Count > 0)
            throw new ConflictException($"'{name}' is used by {string.Join(", ", referencing)}. Remove those fields first.");

        var preview = await schema.PreviewAsync(meta, ct);
        if (preview.TableExists)
        {
            var count = await repository.CountAsync(meta, new ListQuery(), ct);
            if (count > 0)
                throw new ConflictException($"'{name}' has {count} document(s). Delete them first; a DocType with data cannot be deleted.");
            await schema.DropAsync(meta, ct);
        }

        await store.DeleteAsync(name, ct);
        registry.Remove(name);
    }

    /// <summary>Every problem that would stop this definition from being saved, keyed by path.</summary>
    private List<FieldError> Validate(DocTypeMeta definition, DocTypeMeta? existing)
    {
        var errors = DocTypeLoader.Check(definition).ToList();

        for (var i = 0; i < definition.Fields.Count; i++)
        {
            var type = definition.Fields[i].Type;
            if (!FieldTypeCatalog.IsSupported(type))
                errors.Add(new FieldError($"fields[{i}].type", $"{type} fields are not supported yet."));
        }

        if (existing is not null && existing.IsChildTable != definition.IsChildTable)
            errors.Add(new FieldError("isChildTable", "A DocType cannot be turned into a child table, or back, after it is created."));
        if (definition.IsSubmittable)
            errors.Add(new FieldError("isSubmittable", "Submittable DocTypes are not supported yet."));

        errors.AddRange(registry.CheckReferences(definition));

        if (existing is null)
        {
            if (registry.TryGet(definition.Name, out _))
                errors.Add(new FieldError("name", $"A DocType named '{definition.Name}' already exists."));
            else if (FrameworkNames.Contains(definition.Name))
                errors.Add(new FieldError("name", $"'{definition.Name}' is reserved for the framework."));
        }
        else
        {
            for (var i = 0; i < definition.Fields.Count; i++)
            {
                var field = definition.Fields[i];
                var before = existing.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (before is not null && before.Type != field.Type)
                    errors.Add(new FieldError(
                        $"fields[{i}].type",
                        $"The type of the existing field '{field.Name}' cannot be changed yet (it is {before.Type})."));
            }
        }

        return errors;
    }

    private static IReadOnlyList<string> Notes(DocTypeMeta definition, DocTypeMeta? existing)
    {
        if (existing is null)
            return [];

        var kept = existing.Fields
            .Where(old => FieldTypes.HasColumn(old.Type) && definition.Fields.All(f => f.Name != old.Name))
            .Select(old => $"Field '{old.Name}' was removed. Its column and data stay in the database and are hidden.");
        return kept.ToList();
    }

    private static void ThrowIfInvalid(IReadOnlyList<FieldError> errors)
    {
        if (errors.Count > 0)
            throw new DocumentValidationException(errors);
    }

    private void RequireCustom(string name)
    {
        switch (registry.SourceOf(name))
        {
            case null:
                throw new NotFoundException($"DocType '{name}' was not found.");
            case DocTypeSource.Custom:
                return;
            default:
                throw new PermissionDeniedException(
                    $"'{name}' is a standard DocType and cannot be changed here. Customise it instead.");
        }
    }

    private static void RequireDesigner(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();

        if (!user.Roles.Contains(UserContext.AdministratorRole, StringComparer.Ordinal)
            && !user.Roles.Contains(SystemManagerRole, StringComparer.Ordinal))
        {
            throw new PermissionDeniedException("Only a System Manager can design DocTypes.");
        }
    }

    private DateTime Now()
    {
        var utc = clock.GetUtcNow().UtcDateTime;
        return new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
    }
}

/// <summary>Loads the DocTypes saved by the designer into the registry (at startup and before migrations).</summary>
public sealed class CustomDocTypeLoader(IDocTypeStore store, IMutableMetaRegistry registry)
{
    /// <returns>One message per stored definition that could not be loaded; the rest are loaded.</returns>
    public async Task<IReadOnlyList<string>> LoadAsync(CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var row in await store.ListAsync(ct))
        {
            try
            {
                var meta = DocTypeLoader.Parse(row.Definition);
                if (!string.Equals(meta.Name, row.Name, StringComparison.Ordinal))
                    throw new DocTypeValidationException($"stored under '{row.Name}' but defines '{meta.Name}'.");
                if (registry.SourceOf(meta.Name) is DocTypeSource.Core or DocTypeSource.App)
                    throw new DocTypeValidationException("conflicts with a standard DocType of the same name.");

                registry.Upsert(meta, DocTypeSource.Custom);
            }
            catch (DocTypeValidationException ex)
            {
                failures.Add($"Custom DocType '{row.Name}' was skipped: {ex.Message}");
            }
        }

        return failures;
    }
}
