using System.Text.Json;
using Qahwa.Core;

/// <summary>
/// A virtual DocType whose documents are the objects of a JSON file (ADR-158), for reference data that someone else maintains: rates, price
/// lists, codes. Set it with <c>Qahwa:VirtualDocTypes:&lt;DocType&gt; = path/to/file.json</c>. The file is an array of objects, each with a
/// <c>name</c> (text, unique); numbers become decimals, and only keys the DocType has fields for are kept. The file is read again when it changes
/// (never more than once per second), is limited to 5 MB, and a broken file gives an error to the person asking, never partial data.
/// </summary>
public sealed class JsonFileVirtualSource(string docType, string path, TimeProvider? clock = null) : IVirtualSource
{
    public const long MaxBytes = 5 * 1024 * 1024;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private (DateTime Modified, DateTime Checked, List<Dictionary<string, object?>> Rows)? _cache;

    public string DocType { get; } = docType;

    private static object? Value(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetDecimal(out var d) ? d : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null, // nulls, and nested objects or arrays (which no field type holds)
    };

    private List<Dictionary<string, object?>> Rows()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (_cache is { } c && now - c.Checked < TimeSpan.FromSeconds(1))
                return c.Rows;
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new ConflictException($"The file for {DocType} is missing on this server.");
            if (_cache is { } same && same.Modified == info.LastWriteTimeUtc)
            {
                _cache = (same.Modified, now, same.Rows);
                return same.Rows;
            }

            if (info.Length > MaxBytes)
                throw new ConflictException($"The file for {DocType} is larger than {MaxBytes / 1024 / 1024} MB.");
            List<Dictionary<string, object?>> rows = [];
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(path));
                if (json.RootElement.ValueKind != JsonValueKind.Array)
                    throw new ConflictException($"The file for {DocType} must hold an array of objects.");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in json.RootElement.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.Object))
                {
                    var row = item.EnumerateObject().ToDictionary(p => p.Name, p => Value(p.Value), StringComparer.Ordinal);
                    if (row.GetValueOrDefault("name") is not string name || name.Length == 0 || !names.Add(name))
                        continue; // a row without a usable, unique name cannot be a document
                    row["owner"] = "Administrator";
                    row["creation"] = info.LastWriteTimeUtc;
                    row["modified"] = info.LastWriteTimeUtc;
                    row["modified_by"] = "Administrator";
                    row["docstatus"] = 0;
                    rows.Add(row);
                }
            }
            catch (JsonException)
            {
                throw new ConflictException($"The file for {DocType} is not valid JSON.");
            }

            _cache = (info.LastWriteTimeUtc, now, rows);
            return rows;
        }
    }

    public Task<IReadOnlyList<Dictionary<string, object?>>> ListAsync(ListQuery query, CancellationToken ct) => Task.FromResult(VirtualQuery.Apply(Rows(), query));

    public Task<long> CountAsync(ListQuery query, CancellationToken ct) => Task.FromResult(VirtualQuery.Count(Rows(), query));

    public Task<Dictionary<string, object?>?> GetAsync(string name, CancellationToken ct) =>
        Task.FromResult(Rows().FirstOrDefault(r => (string)r["name"]! == name));
}
