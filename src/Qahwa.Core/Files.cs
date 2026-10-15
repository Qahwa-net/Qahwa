using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Qahwa.Abstractions;
using Qahwa.Core.Designer;

namespace Qahwa.Core;

/// <summary>Where file contents live. Keys are random; the framework never builds a path from user input.</summary>
public interface IFileStorage
{
    /// <summary>Saves what is read from <paramref name="content"/> under a new random key and returns the key.</summary>
    Task<string> SaveAsync(Stream content, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);
}

/// <summary>Settings for attachments.</summary>
public sealed class AttachmentOptions
{
    /// <summary>The largest file, in megabytes (1 to 100).</summary>
    public int MaxMegabytes { get; set; } = 25;
}

public sealed record FileInfoItem(string Id, string FileName, string ContentType, long Size, bool Image, string UploadedBy, string? UploadedByName, DateTime Created);

/// <summary>A file ready to be sent: the contents, the type to send it as and whether the browser may show it in the page.</summary>
public sealed record FileDownload(Stream Content, string FileName, string ContentType, long Size, bool Inline);

/// <summary>
/// Attachments: files kept with a document. Uploading needs write access to the document and downloading read access, checked
/// on every request. Files are never served from their own address or by type: the type is worked out from the content, only
/// plain pictures are shown inline, everything else downloads as a file.
/// </summary>
public sealed class FileService(
    IMetaRegistry registry,
    IDocumentRepository repository,
    DocumentService documents,
    PermissionService permissions,
    NotificationService notifications,
    IFileStorage storage,
    HistoryRecorder history)
{
    public const string DocTypeName = "File";
    public const int MaxPerDocument = 50;
    public const int MaxFileName = 140;

    /// <summary>The size limit of one file, in bytes (25 MB unless configured).</summary>
    public long MaxBytes { get; init; } = 25L * 1024 * 1024;

    private DocTypeMeta Meta => registry.Get(DocTypeName);

    private static DocumentValidationException Invalid(string field, string message) => new([new FieldError(field, message)]);

    private bool IsManager(IUserContext user) =>
        permissions.IsAdministrator(user) || user.Roles.Contains("System Manager", StringComparer.Ordinal);

    /// <summary>A name that is safe to show and to send in a header: no folders, no control characters, not empty, not too long.</summary>
    public static string CleanName(string? name)
    {
        var text = (name ?? string.Empty).Replace('\\', '/');
        text = text[(text.LastIndexOf('/') + 1)..];
        var cleaned = new string(text.Select(c => char.IsControl(c) || c is '"' or '<' or '>' or ':' or '|' or '?' or '*' ? '_' : c).ToArray()).Trim().Trim('.');
        if (cleaned.Length > MaxFileName)
        {
            var ext = Path.GetExtension(cleaned);
            cleaned = ext.Length is > 0 and <= 12 ? cleaned[..(MaxFileName - ext.Length)] + ext : cleaned[..MaxFileName];
        }

        return cleaned.Length == 0 ? "file" : cleaned;
    }

    /// <summary>The content type to send a file as, from its first bytes: a real picture type, or "application/octet-stream" for everything else.</summary>
    public static string Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
            return "image/jpeg";
        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";
        return "application/octet-stream";
    }

    private static bool IsInlineImage(string contentType) => contentType is "image/png" or "image/jpeg" or "image/gif" or "image/webp";

    private async Task<FileInfoItem> ToInfoAsync(Dictionary<string, object?> row, CancellationToken ct)
    {
        var by = (string)row["owner"]!;
        var type = row["content_type"] as string ?? "application/octet-stream";
        return new FileInfoItem((string)row["name"]!, (string)row["file_name"]!, type, Convert.ToInt64(row["file_size"] ?? 0L, CultureInfo.InvariantCulture),
            IsInlineImage(type), by, await notifications.NameOfAsync(by, ct), (DateTime)row["creation"]!);
    }

    private async Task RequireWriteAsync(string doctype, string name, IUserContext user, CancellationToken ct)
    {
        await documents.GetAsync(doctype, name, user, ct); // reading the document is the least it takes
        var meta = registry.Get(doctype);
        if (permissions.Evaluate(meta, user, PermAction.Write) == AccessLevel.None)
            throw new PermissionDeniedException("You need permission to change this document to attach files to it.");
    }

    private async Task NoteAsync(string doctype, string name, string text, string user, CancellationToken ct)
    {
        if (history.InfoNote(doctype, name, text, user, history.Now()) is { } note)
            await repository.InsertAsync(note.Meta, note.Inserts[0], null, ct);
    }

    public async Task<IReadOnlyList<FileInfoItem>> ListAsync(string doctype, string name, IUserContext user, CancellationToken ct = default)
    {
        await documents.GetAsync(doctype, name, user, ct);
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            OrderBy = "creation", Descending = false, Limit = MaxPerDocument,
        }, ct);
        var result = new List<FileInfoItem>();
        foreach (var row in rows)
            result.Add(await ToInfoAsync(row, ct));
        return result;
    }

    /// <summary>Attaches a file. The content is copied once, counted as it goes, and refused when it is empty or over the limit.</summary>
    public async Task<FileInfoItem> UploadAsync(string doctype, string name, string? fileName, Stream content, IUserContext user, CancellationToken ct = default)
    {
        await RequireWriteAsync(doctype, name, user, ct);
        if (registry.Get(doctype).IsChildTable)
            throw Invalid(string.Empty, "Files are attached to documents.");
        var existing = await repository.CountAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
        }, ct);
        if (existing >= MaxPerDocument)
            throw Invalid("file", $"A document can have at most {MaxPerDocument} files.");

        var head = new byte[16];
        var counted = new CountingStream(content, MaxBytes, head);
        var key = await storage.SaveAsync(counted, ct);
        try
        {
            if (counted.Exceeded)
                throw Invalid("file", $"The file is larger than {MaxBytes / (1024 * 1024)} MB.");
            if (counted.Length == 0)
                throw Invalid("file", "The file is empty.");

            var type = Sniff(head.AsSpan(0, (int)Math.Min(counted.Length, head.Length)));
            var row = HistoryRecorder.Standard(user.User, history.Now());
            var clean = CleanName(fileName);
            row["file_name"] = clean;
            row["content_type"] = type;
            row["file_size"] = counted.Length;
            row["content_hash"] = Convert.ToHexString(counted.Hash()).ToLowerInvariant();
            row["storage_key"] = key;
            row["reference_doctype"] = doctype;
            row["reference_name"] = name;
            await repository.InsertAsync(Meta, row, null, ct);
            await NoteAsync(doctype, name, $"Attached {clean}", user.User, ct);
            return await ToInfoAsync(row, ct);
        }
        catch
        {
            await storage.DeleteAsync(key, CancellationToken.None); // never keep the bytes of a file that was refused
            throw;
        }
    }

    private async Task<Dictionary<string, object?>> RowAsync(string id, CancellationToken ct) =>
        await repository.GetAsync(Meta, id, ct) ?? throw new NotFoundException("File not found.");

    /// <summary>Opens a file for download. The person must be able to read the document it belongs to; otherwise the file "does not exist".</summary>
    public async Task<FileDownload> OpenAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await RowAsync(id, ct);
        try
        {
            await documents.GetAsync((string)row["reference_doctype"]!, (string)row["reference_name"]!, user, ct);
        }
        catch (Exception ex) when (ex is PermissionDeniedException or NotFoundException)
        {
            throw new NotFoundException("File not found.");
        }

        var type = row["content_type"] as string ?? "application/octet-stream";
        var stream = await storage.OpenReadAsync((string)row["storage_key"]!, ct);
        return new FileDownload(stream, (string)row["file_name"]!, IsInlineImage(type) ? type : "application/octet-stream",
            Convert.ToInt64(row["file_size"] ?? 0L, CultureInfo.InvariantCulture), IsInlineImage(type));
    }

    /// <summary>Removes a file: by whoever uploaded it or can change the document, or a System Manager.</summary>
    public async Task DeleteAsync(string id, IUserContext user, CancellationToken ct = default)
    {
        if (!user.IsAuthenticated)
            throw new UnauthenticatedException();
        var row = await RowAsync(id, ct);
        var doctype = (string)row["reference_doctype"]!;
        var name = (string)row["reference_name"]!;
        try
        {
            await documents.GetAsync(doctype, name, user, ct);
        }
        catch (Exception ex) when (ex is PermissionDeniedException or NotFoundException)
        {
            throw new NotFoundException("File not found.");
        }

        var mine = string.Equals(row["owner"] as string, user.User, StringComparison.Ordinal);
        if (!mine && !IsManager(user) && permissions.Evaluate(registry.Get(doctype), user, PermAction.Write) == AccessLevel.None)
            throw new PermissionDeniedException("You may not remove this file.");

        await repository.DeleteAsync(Meta, id, null, ct);
        await storage.DeleteAsync((string)row["storage_key"]!, ct);
        await NoteAsync(doctype, name, $"Removed {row["file_name"]}", user.User, ct);
    }

    /// <summary>A document is deleted: its files go too, rows and contents.</summary>
    public async Task DeleteForDocumentAsync(string doctype, string name, CancellationToken ct = default)
    {
        var rows = await repository.ListAsync(Meta, new ListQuery
        {
            Filters = [new("reference_doctype", FilterOp.Eq, doctype), new("reference_name", FilterOp.Eq, name)],
            Fields = ["name", "storage_key"], Limit = 1000,
        }, ct);
        foreach (var row in rows)
        {
            await repository.DeleteAsync(Meta, (string)row["name"]!, null, ct);
            try
            {
                await storage.DeleteAsync((string)row["storage_key"]!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An orphaned blob is harmless and can be swept; the document delete must not fail for it.
            }
        }
    }

    /// <summary>Passes bytes through while counting them, remembering the first few and a running SHA-256, and stopping at the limit.</summary>
    private sealed class CountingStream(Stream inner, long limit, byte[] head) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private long _length;

        public bool Exceeded { get; private set; }

        public byte[] Hash() => _hash.GetHashAndReset();

        public override long Length => _length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Position { get => _length; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (Exceeded)
                return 0;
            var read = inner.Read(buffer);
            Account(buffer[..read]);
            return Exceeded ? 0 : read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Exceeded)
                return 0;
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Account(buffer.Span[..read]);
            return Exceeded ? 0 : read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Account(ReadOnlySpan<byte> chunk)
        {
            if (chunk.Length == 0)
                return;
            if (_length < head.Length)
                chunk[..(int)Math.Min(chunk.Length, head.Length - _length)].CopyTo(head.AsSpan((int)_length));
            _length += chunk.Length;
            if (_length > limit)
            {
                Exceeded = true;
                return;
            }

            _hash.AppendData(chunk);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
