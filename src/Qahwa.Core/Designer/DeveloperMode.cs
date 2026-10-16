using System.Text.RegularExpressions;

namespace Qahwa.Core.Designer;

/// <summary>Developer mode (<c>Qahwa:Developer</c>): DocTypes made in the Designer are also written as JSON files into an app's folder, so they can be committed and shipped.</summary>
public sealed class DeveloperOptions
{
    public bool Enabled { get; set; }

    /// <summary>The folder the files go to (usually the app's DocType folder). Required when <see cref="Enabled"/>.</summary>
    public string? AppPath { get; set; }
}

/// <summary>Told when a DocType made in the Designer changes. <c>definition</c> is null when it was deleted.</summary>
public interface IDeveloperExport
{
    Task ChangedAsync(string name, string? definition, CancellationToken ct = default);
}

/// <summary>
/// Writes the definitions to disk. The file name comes from the DocType's name reduced to lower-case letters, digits and underscores, the
/// full path must stay inside the configured folder, and a file is written whole (to a temporary file first, then moved into place), so a
/// half-written definition is never left behind. A problem is reported to the caller, who never lets it undo the change in the Designer.
/// </summary>
public sealed partial class DeveloperExport(DeveloperOptions options) : IDeveloperExport
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NotAllowed();

    public bool Enabled => options.Enabled && !string.IsNullOrWhiteSpace(options.AppPath);

    /// <summary>The settings must make sense; checked when the server starts.</summary>
    public static string? Problem(DeveloperOptions o) =>
        o.Enabled && string.IsNullOrWhiteSpace(o.AppPath) ? "Qahwa:Developer:AppPath must name a folder when developer mode is on." : null;

    public static string FileNameFor(string docType)
    {
        var slug = NotAllowed().Replace(docType.ToLowerInvariant(), "_").Trim('_');
        if (slug.Length == 0)
            throw new ArgumentException("The DocType name cannot be used for a file name.", nameof(docType));
        return (slug.Length > 80 ? slug[..80] : slug) + ".json";
    }

    private string PathFor(string docType)
    {
        var root = Path.GetFullPath(options.AppPath!);
        var full = Path.GetFullPath(Path.Combine(root, FileNameFor(docType)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? full : throw new InvalidOperationException("The file would be outside the app folder.");
    }

    public async Task ChangedAsync(string name, string? definition, CancellationToken ct = default)
    {
        if (!Enabled)
            return;
        var path = PathFor(name);
        if (definition is null)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, definition.TrimEnd() + Environment.NewLine, new System.Text.UTF8Encoding(false), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }

    /// <summary>Writes every given definition (used to export what was made before developer mode was switched on).</summary>
    public async Task<int> WriteAllAsync(IEnumerable<StoredDocType> all, CancellationToken ct = default)
    {
        if (!Enabled)
            throw new InvalidOperationException("Developer mode is not switched on.");
        var count = 0;
        foreach (var row in all)
        {
            await ChangedAsync(row.Name, row.Definition, ct);
            count++;
        }

        return count;
    }
}
