using System.Text.RegularExpressions;
using Qahwa.Core;

/// <summary>
/// Keeps files on disk under a folder, in sub-folders by the first two characters of a random key. A name is never taken from
/// a person: keys are 32 random hex digits, checked on every use, so no path can escape the folder.
/// </summary>
public sealed partial class DiskFileStorage : IFileStorage
{
    private readonly string _root;

    public DiskFileStorage(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex KeyRegex();

    private string PathOf(string key)
    {
        if (!KeyRegex().IsMatch(key))
            throw new ArgumentException("Not a file key.", nameof(key));
        return Path.Combine(_root, key[..2], key);
    }

    public async Task<string> SaveAsync(Stream content, CancellationToken ct = default)
    {
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(PathOf(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathOf(key);
        if (File.Exists(path))
            File.Delete(path);
        return Task.CompletedTask;
    }
}
