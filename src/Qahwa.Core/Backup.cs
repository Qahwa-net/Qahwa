using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Qahwa.Core;

public sealed class BackupOptions
{
    /// <summary>Where scheduled backups are written (a folder). Empty means no scheduled backups.</summary>
    public string? Path { get; set; }

    /// <summary>How many scheduled backups are kept; older ones are deleted.</summary>
    public int Keep { get; set; } = 7;

    /// <summary>The most a backup may be when read back (after unzipping). Protects against files made to blow up.</summary>
    public int MaxRestoreMegabytes { get; set; } = 1024;
}

public sealed record BackupTable(string DocType, string File, long Rows);

public sealed record BackupManifest(string Format, int Version, DateTime CreatedAt, IReadOnlyList<BackupTable> Tables, IReadOnlyList<SeriesCounter> Series);

public sealed record RestoreTableReport(string DocType, long Inserted, long Existing, long Failed, string? FirstProblem);

public sealed record RestoreReport(bool DryRun, IReadOnlyList<BundleItemResult> Definitions, IReadOnlyList<RestoreTableReport> Tables, int SeriesRaised);

/// <summary>
/// A portable backup of the documents and the definitions made in the Desk, as one zip: <c>manifest.json</c>, <c>bundle.json</c> (the
/// definitions, see <see cref="BundleService"/>) and one <c>data/N.jsonl</c> (a row per line) for every DocType, child tables included.
/// It is **not** a point-in-time snapshot (tables are read one after the other), and it deliberately leaves out what must never be in a file
/// that gets copied around: passwords, two-factor secrets and sessions (people set a password again after a restore), uploaded file contents
/// (back up the files folder with your usual tools; the File rows are kept) and short-lived queues (emails waiting, webhook deliveries, the
/// error log). For a database that must be restored to a moment in time, also use the database's own tools (for PostgreSQL, pg_dump).
/// Restoring only ever **adds**: a row whose name already exists is left alone. Only an Administrator may make or restore a backup,
/// because both bypass the rules about who may see or change each document.
/// </summary>
public sealed class BackupService(IMetaRegistry registry, IDocumentRepository repository, BundleService bundle, PermissionService permissions, TimeProvider clock)
{
    public const string Format = "qahwa-backup";
    public const int Version = 1;
    private const int PageSize = 500;
    private const int MaxLineBytes = 1024 * 1024;

    /// <summary>Queues and logs that are not worth keeping and may contain things that should not travel.</summary>
    public static readonly IReadOnlySet<string> Skipped = new HashSet<string>(StringComparer.Ordinal) { "Email Account", "Email Queue", "Webhook Delivery", "Error Log", "Patch Log" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>Throws unless the person may make or restore backups (call before starting to send a file, so a refusal can still be a normal error).</summary>
    public void Require(IUserContext user) => RequireAdministrator(user);

    private void RequireAdministrator(IUserContext user)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        if (!permissions.IsAdministrator(user))
            throw new PermissionDeniedException("Only an Administrator can make or restore a backup.");
    }

    private IReadOnlyList<DocTypeMeta> Included() =>
        registry.All.Where(m => !Skipped.Contains(m.Name)).OrderBy(m => m.IsChildTable).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();

    // ---- making a backup ----

    public async Task WriteAsync(Stream output, IUserContext user, CancellationToken ct = default)
    {
        RequireAdministrator(user);
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        var tables = new List<BackupTable>();

        await WriteTextAsync(zip, "bundle.json", BundleService.ToJson(await bundle.ExportAsync("backup", null, user, ct)), ct);

        var index = 0;
        foreach (var meta in Included())
        {
            ct.ThrowIfCancellationRequested();
            var file = $"data/{index++}.jsonl";
            long rows = 0;
            var entry = zip.CreateEntry(file, CompressionLevel.Optimal);
            await using (var stream = entry.Open())
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                for (var offset = 0; ; offset += PageSize)
                {
                    var page = await repository.ListAsync(meta, new ListQuery { OrderBy = "name", Descending = false, Limit = PageSize, Offset = offset }, ct);
                    foreach (var row in page)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(row, Json));
                        rows++;
                    }

                    if (page.Count < PageSize)
                        break;
                }
            }

            tables.Add(new BackupTable(meta.Name, file, rows));
        }

        var manifest = new BackupManifest(Format, Version, clock.GetUtcNow().UtcDateTime, tables, await repository.ListSeriesAsync(ct));
        await WriteTextAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json), ct);
    }

    private static async Task WriteTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
    }

    // ---- reading one back ----

    /// <param name="input">A seekable stream holding the zip.</param>
    public async Task<RestoreReport> RestoreAsync(Stream input, bool dryRun, IUserContext user, CancellationToken ct = default, long maxBytes = 1024L * 1024 * 1024)
    {
        RequireAdministrator(user);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new DocumentValidationException("This is not a backup file (it is not a zip).");
        }

        using (zip)
        {
            var budget = new Budget(maxBytes);
            var manifest = await ReadManifestAsync(zip, budget, ct);

            // The definitions first, so the DocTypes a backup depends on exist before their rows are added.
            var definitions = new List<BundleItemResult>();
            if (zip.GetEntry("bundle.json") is { } bundleEntry)
            {
                var text = await ReadAllAsync(bundleEntry, budget, ct);
                definitions.AddRange((await bundle.ImportAsync(BundleService.FromJson(text), dryRun, user, ct)).Items);
            }

            var reports = new List<RestoreTableReport>();
            foreach (var table in manifest.Tables)
            {
                ct.ThrowIfCancellationRequested();
                if (Skipped.Contains(table.DocType) || !registry.TryGet(table.DocType, out var meta))
                {
                    reports.Add(new RestoreTableReport(table.DocType, 0, 0, table.Rows, Skipped.Contains(table.DocType) ? "Not restored by design." : "This DocType does not exist here."));
                    continue;
                }

                var entry = zip.GetEntry(table.File);
                if (entry is null || !System.Text.RegularExpressions.Regex.IsMatch(table.File, @"^data/\d{1,5}\.jsonl$"))
                {
                    reports.Add(new RestoreTableReport(table.DocType, 0, 0, table.Rows, "The data for this DocType is missing from the file."));
                    continue;
                }

                reports.Add(await RestoreTableAsync(meta, entry, dryRun, budget, ct));
            }

            var raised = 0;
            var current = (await repository.ListSeriesAsync(ct)).ToDictionary(s => s.Prefix, s => s.Current, StringComparer.Ordinal);
            foreach (var series in manifest.Series.Where(s => s.Prefix.Length is > 0 and <= 140 && s.Current > 0))
            {
                if (current.GetValueOrDefault(series.Prefix) >= series.Current)
                    continue;
                if (!dryRun)
                    await repository.SetSeriesAsync(series.Prefix, series.Current, ct);
                raised++;
            }

            return new RestoreReport(dryRun, definitions, reports, raised);
        }
    }

    private sealed class Budget(long max)
    {
        private long _used;

        public void Add(long bytes)
        {
            _used += bytes;
            if (_used > max)
                throw new DocumentValidationException("This backup is larger than this system will read back.");
        }
    }

    private static async Task<string> ReadAllAsync(ZipArchiveEntry entry, Budget budget, CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            budget.Add(read);
            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private async Task<BackupManifest> ReadManifestAsync(ZipArchive zip, Budget budget, CancellationToken ct)
    {
        var entry = zip.GetEntry("manifest.json") ?? throw new DocumentValidationException("This is not a Qahwa backup (no manifest).");
        BackupManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<BackupManifest>(await ReadAllAsync(entry, budget, ct), Json);
        }
        catch (JsonException)
        {
            throw new DocumentValidationException("The backup's manifest is damaged.");
        }

        if (manifest is null || manifest.Format != Format)
            throw new DocumentValidationException("This is not a Qahwa backup.");
        if (manifest.Version != Version)
            throw new DocumentValidationException($"This backup has a version this server does not understand (expected {Version}).");
        return manifest with { Tables = manifest.Tables ?? [], Series = manifest.Series ?? [] };
    }

    private async Task<RestoreTableReport> RestoreTableAsync(DocTypeMeta meta, ZipArchiveEntry entry, bool dryRun, Budget budget, CancellationToken ct)
    {
        long inserted = 0, existing = 0, failed = 0;
        string? problem = null;
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            budget.Add(line.Length + 1);
            if (line.Length == 0)
                continue;
            if (line.Length > MaxLineBytes)
            {
                failed++;
                problem ??= "A row is too large.";
                continue;
            }

            try
            {
                var values = RowFromJson(meta, line);
                var name = (string)values["name"]!;
                if (await repository.ExistsAsync(meta, name, ct))
                {
                    existing++;
                    continue;
                }

                if (!dryRun)
                    await repository.InsertAsync(meta, values, null, ct);
                inserted++;
            }
            catch (ConflictException)
            {
                existing++; // a unique value is already used by another row: leave both as they are
            }
            catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
            {
                failed++;
                problem ??= ex.Message;
            }
        }

        return new RestoreTableReport(meta.Name, inserted, existing, failed, problem);
    }

    private static readonly string[] Standard = ["name", "owner", "creation", "modified", "modified_by", "docstatus", "idx", "parent", "parenttype", "parentfield"];

    /// <summary>One line of a backup as the values the database layer stores. Only columns of the DocType are taken; everything else is ignored.</summary>
    internal static Dictionary<string, object?> RowFromJson(DocTypeMeta meta, string line)
    {
        using var document = JsonDocument.Parse(line);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new FormatException("A row is not an object.");

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var fields = meta.Fields.Where(f => FieldTypes.HasColumn(f.Type)).ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (fields.TryGetValue(property.Name, out var field))
                values[property.Name] = FieldValueConverter.ToStorage(field.Type, property.Value.Clone());
            else if (Standard.Contains(property.Name))
                values[property.Name] = Standard_(property.Name, property.Value);
        }

        if (values.GetValueOrDefault("name") is not string { Length: > 0 and <= 140 } name || name.Any(char.IsControl))
            throw new FormatException("A row has no valid name.");
        if (meta.IsChildTable && (values.GetValueOrDefault("parent") is not string || values.GetValueOrDefault("parenttype") is not string || values.GetValueOrDefault("parentfield") is not string))
            throw new FormatException("A child row does not say which document it belongs to.");

        var now = DateTime.UtcNow;
        values.TryAdd("creation", now);
        values.TryAdd("modified", values["creation"]);
        values.TryAdd("owner", "Administrator");
        values.TryAdd("modified_by", values["owner"]);
        values.TryAdd("docstatus", 0);
        values.TryAdd("idx", 0);
        return values;
    }

    private static object? Standard_(string name, JsonElement value) => name switch
    {
        "creation" or "modified" => value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date)
            ? DateTime.SpecifyKind(date, DateTimeKind.Utc) : throw new FormatException($"'{name}' is not a date."),
        "docstatus" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var status) && status is >= 0 and <= 2 ? status : throw new FormatException("'docstatus' is not 0, 1 or 2."),
        "idx" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var idx) ? idx : throw new FormatException("'idx' is not a number."),
        _ => value.ValueKind is JsonValueKind.String or JsonValueKind.Null ? value.GetString() : throw new FormatException($"'{name}' is not text."),
    };

    // ---- scheduled backups ----

    /// <summary>Writes today's backup into the folder unless one exists, keeps only the newest <paramref name="keep"/>, and says what it wrote.</summary>
    public async Task<string?> RunScheduledAsync(string folder, int keep, IUserContext user, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var now = clock.GetUtcNow().UtcDateTime;
        var path = Path.Combine(folder, $"qahwa-backup-{now:yyyyMMdd}.zip");
        string? made = null;
        if (File.Exists(path + ".part") && File.GetLastWriteTimeUtc(path + ".part") <= now.AddHours(-6))
            File.Delete(path + ".part"); // left behind by a server that died while writing

        if (!File.Exists(path))
        {
            var created = false;
            try
            {
                // CreateNew: with several servers sharing the folder, whichever creates the file makes the day's backup, the others see it exists.
                await using (var file = new FileStream(path + ".part", FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    await WriteAsync(file, user, ct);
                }

                File.Move(path + ".part", path, overwrite: false);
                made = path;
            }
            catch (IOException) when (File.Exists(path) || !created)
            {
                // another server made today's file, or is making it right now
                if (created && File.Exists(path + ".part"))
                    File.Delete(path + ".part");
            }
            catch
            {
                if (created && File.Exists(path + ".part"))
                    File.Delete(path + ".part");
                throw;
            }
        }

        var old = Directory.GetFiles(folder, "qahwa-backup-????????.zip").OrderByDescending(f => f, StringComparer.Ordinal).Skip(Math.Max(keep, 1));
        foreach (var file in old)
            File.Delete(file);
        return made;
    }
}
