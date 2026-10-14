using System.Collections.Immutable;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>Where a DocType definition comes from, which decides whether it can be edited in the UI.</summary>
public enum DocTypeSource
{
    /// <summary>Ships inside the framework (User, Role, ...). Read-only.</summary>
    Core,

    /// <summary>Loaded from an app's JSON files. Read-only in the UI; extend it with customisations instead.</summary>
    App,

    /// <summary>Created in the DocType Designer and stored in the database.</summary>
    Custom,
}

public interface IMetaRegistry
{
    IReadOnlyCollection<DocTypeMeta> All { get; }

    bool TryGet(string name, out DocTypeMeta meta);

    /// <exception cref="NotFoundException">No such DocType.</exception>
    DocTypeMeta Get(string name);

    DocTypeSource? SourceOf(string name);

    /// <summary>Problems with the Link and Table targets of a definition, judged against the current registry.</summary>
    IReadOnlyList<FieldError> CheckReferences(DocTypeMeta meta);
}

/// <summary>A registry whose DocTypes can change while the application runs (used by the designer).</summary>
public interface IMutableMetaRegistry : IMetaRegistry
{
    void Upsert(DocTypeMeta meta, DocTypeSource source);

    bool Remove(string name);
}

/// <summary>
/// Thread-safe registry. Readers see an immutable snapshot, so a designer change can never
/// expose a half-updated set of DocTypes to a request that is already running.
/// </summary>
public sealed class MetaRegistry : IMutableMetaRegistry
{
    private sealed record Snapshot(
        ImmutableDictionary<string, (DocTypeMeta Meta, DocTypeSource Source)> ByName,
        IReadOnlyCollection<DocTypeMeta> All);

    private readonly object _gate = new();
    private Snapshot _snapshot = Build(ImmutableDictionary.Create<string, (DocTypeMeta, DocTypeSource)>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyCollection<DocTypeMeta> All => _snapshot.All;

    public bool TryGet(string name, out DocTypeMeta meta)
    {
        if (_snapshot.ByName.TryGetValue(name, out var entry))
        {
            meta = entry.Meta;
            return true;
        }

        meta = null!;
        return false;
    }

    public DocTypeMeta Get(string name) =>
        TryGet(name, out var meta) ? meta : throw new NotFoundException($"DocType '{name}' was not found.");

    public DocTypeSource? SourceOf(string name) =>
        _snapshot.ByName.TryGetValue(name, out var entry) ? entry.Source : null;

    public void Add(DocTypeMeta meta, DocTypeSource source = DocTypeSource.App)
    {
        DocTypeLoader.Validate(meta);
        lock (_gate)
        {
            if (_snapshot.ByName.ContainsKey(meta.Name))
                throw new DocTypeValidationException($"Duplicate DocType '{meta.Name}'.");
            _snapshot = Build(_snapshot.ByName.Add(meta.Name, (meta, source)));
        }
    }

    public void Upsert(DocTypeMeta meta, DocTypeSource source)
    {
        DocTypeLoader.Validate(meta);
        lock (_gate)
            _snapshot = Build(_snapshot.ByName.SetItem(meta.Name, (meta, source)));
    }

    public bool Remove(string name)
    {
        lock (_gate)
        {
            if (!_snapshot.ByName.ContainsKey(name))
                return false;
            _snapshot = Build(_snapshot.ByName.Remove(name));
            return true;
        }
    }

    /// <summary>Adds the framework's own DocTypes (User, Role, Has Role) that ship inside this assembly.</summary>
    public void AddCoreDocTypes()
    {
        var assembly = typeof(MetaRegistry).Assembly;
        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.Contains(".CoreDocTypes.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        foreach (var resource in resources)
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            Add(DocTypeLoader.Parse(reader.ReadToEnd()), DocTypeSource.Core);
        }
    }

    public void LoadDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"DocType directory not found: {path}");

        foreach (var file in Directory.EnumerateFiles(path, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            Add(DocTypeLoader.LoadFile(file));
    }

    /// <summary>Checks cross-DocType references (Link and Table targets). Call after everything is loaded.</summary>
    public void ValidateReferences()
    {
        foreach (var meta in All)
        {
            var errors = CheckReferences(meta);
            if (errors.Count > 0)
                throw new DocTypeValidationException($"{meta.Name}: {errors[0].Message}", errors);
        }
    }

    /// <summary>Problems with the Link and Table targets of one definition, judged against the current registry.</summary>
    public IReadOnlyList<FieldError> CheckReferences(DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        for (var i = 0; i < meta.Fields.Count; i++)
        {
            var field = meta.Fields[i];
            if (field.Type is not (FieldType.Link or FieldType.Table) || string.IsNullOrWhiteSpace(field.Options))
                continue;

            // A DocType may link to itself.
            var target = string.Equals(field.Options, meta.Name, StringComparison.OrdinalIgnoreCase)
                ? meta
                : TryGet(field.Options, out var found) ? found : null;

            if (target is null)
                errors.Add(new FieldError($"fields[{i}].options", $"The DocType '{field.Options}' does not exist."));
            else if (field.Type == FieldType.Table && !target.IsChildTable)
                errors.Add(new FieldError($"fields[{i}].options", $"'{target.Name}' must be a child table."));
        }

        // "fetch from" copies a value out of the linked document, so that field must exist there and fit this one.
        for (var i = 0; i < meta.Fields.Count; i++)
        {
            var field = meta.Fields[i];
            var parts = field.FetchFrom?.Split('.');
            if (parts is not { Length: 2 })
                continue;

            var link = meta.Fields.FirstOrDefault(f => f.Name == parts[0] && f.Type == FieldType.Link);
            if (link?.Options is null || !TryGet(link.Options, out var linked))
                continue; // the missing link target is reported above

            if (!linked.ColumnTypes().TryGetValue(parts[1], out var type))
                errors.Add(new FieldError($"fields[{i}].fetchFrom", $"{linked.Name} has no field '{parts[1]}'."));
            else if (type != field.Type && !(FieldTypes.IsTextual(type) && FieldTypes.IsTextual(field.Type)))
                errors.Add(new FieldError($"fields[{i}].fetchFrom", $"'{parts[1]}' is a {type} field and cannot be copied into a {field.Type} field."));
        }

        return errors;
    }

    private static Snapshot Build(ImmutableDictionary<string, (DocTypeMeta Meta, DocTypeSource Source)> byName) =>
        new(byName, byName.Values.Select(v => v.Meta).ToArray());
}
