using Qahwa.Abstractions;

namespace Qahwa.Core;

public interface IMetaRegistry
{
    IReadOnlyCollection<DocTypeMeta> All { get; }

    bool TryGet(string name, out DocTypeMeta meta);

    /// <exception cref="NotFoundException">No such DocType.</exception>
    DocTypeMeta Get(string name);
}

public sealed class MetaRegistry : IMetaRegistry
{
    private readonly Dictionary<string, DocTypeMeta> _byName = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<DocTypeMeta> All => _byName.Values;

    public bool TryGet(string name, out DocTypeMeta meta) => _byName.TryGetValue(name, out meta!);

    public DocTypeMeta Get(string name) =>
        _byName.TryGetValue(name, out var meta) ? meta : throw new NotFoundException($"DocType '{name}' was not found.");

    public void Add(DocTypeMeta meta)
    {
        DocTypeLoader.Validate(meta);
        if (!_byName.TryAdd(meta.Name, meta))
            throw new DocTypeValidationException($"Duplicate DocType '{meta.Name}'.");
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
        foreach (var meta in _byName.Values)
        {
            foreach (var field in meta.Fields.Where(f => f.Type is FieldType.Link or FieldType.Table))
            {
                if (!_byName.TryGetValue(field.Options!, out var target))
                    throw new DocTypeValidationException(
                        $"{meta.Name}.{field.Name}: target DocType '{field.Options}' does not exist.");
                if (field.Type == FieldType.Table && !target.IsChildTable)
                    throw new DocTypeValidationException(
                        $"{meta.Name}.{field.Name}: '{target.Name}' must be a child table (isChildTable).");
            }
        }
    }
}
