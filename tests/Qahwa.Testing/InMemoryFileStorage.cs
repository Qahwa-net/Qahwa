using System.Collections.Concurrent;
using Qahwa.Core;

namespace Qahwa.Testing;

public sealed class InMemoryFileStorage : IFileStorage
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();

    public int Count => _files.Count;

    public bool Has(string key) => _files.ContainsKey(key);

    public IReadOnlyCollection<string> Keys => _files.Keys.ToList();

    public async Task<string> SaveAsync(Stream content, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _files[key] = buffer.ToArray();
        return key;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        _files.TryGetValue(key, out var bytes) ? Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)) : throw new FileNotFoundException();

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _files.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}
