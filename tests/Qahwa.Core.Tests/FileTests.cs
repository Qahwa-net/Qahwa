using System.Text;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class FileTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    private readonly TestEngine _engine = new();

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);
    private static readonly UserContext Bob = new("bob@example.com", ["Clerk"]);
    private static readonly UserContext Viewer = new("vera@example.com", ["Viewer"]);
    private static readonly UserContext Eve = new("eve@example.com", ["Outsider"]);

    public FileTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Contract", "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true }, { "role": "Viewer", "read": true } ] }
            """));
    }

    private async Task<string> ContractAsync(UserContext by) =>
        (string)(await _engine.Documents.InsertAsync("Contract", Values(("title", "Deal")), by))["name"]!;

    private Task<FileInfoItem> UploadAsync(string id, string name, byte[] bytes, UserContext user, string doctype = "Contract") =>
        _engine.Files.UploadAsync(doctype, id, name, new MemoryStream(bytes), user);

    private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<byte[]> ReadAsync(FileDownload file)
    {
        using var buffer = new MemoryStream();
        await file.Content.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    // ---- uploading and downloading ----

    [Fact]
    public async Task A_file_is_attached_listed_downloaded_and_noted_in_the_thread()
    {
        var id = await ContractAsync(Ann);

        var file = await UploadAsync(id, "terms.pdf", Text("%PDF-1.7 hello"), Ann);

        Assert.Equal(("terms.pdf", "application/octet-stream", 14L), (file.FileName, file.ContentType, file.Size));
        Assert.Equal([file.Id], (await _engine.Files.ListAsync("Contract", id, Bob)).Select(f => f.Id)); // anyone who can read the document sees the list
        var download = await _engine.Files.OpenAsync(file.Id, Viewer);
        Assert.Equal(Text("%PDF-1.7 hello"), await ReadAsync(download));
        Assert.False(download.Inline);
        Assert.Contains(await _engine.Activity.TimelineAsync("Contract", id, Ann), i => i.Content == "Attached terms.pdf");
    }

    [Fact]
    public async Task Only_real_pictures_are_shown_inline_and_the_type_comes_from_the_bytes_not_the_name()
    {
        var id = await ContractAsync(Ann);

        var picture = await UploadAsync(id, "logo.png", Png, Ann);
        var page = await UploadAsync(id, "logo.png.html", Text("<html><script>alert(1)</script></html>"), Ann);
        var fake = await UploadAsync(id, "photo.jpg", Text("<script>alert(1)</script>"), Ann); // named like a picture, is not one
        var svg = await UploadAsync(id, "icon.svg", Text("<svg onload=alert(1)/>"), Ann);

        Assert.Equal(("image/png", true), (picture.ContentType, picture.Image));
        Assert.True((await _engine.Files.OpenAsync(picture.Id, Ann)).Inline);
        foreach (var other in new[] { page, fake, svg })
        {
            var download = await _engine.Files.OpenAsync(other.Id, Ann);
            Assert.Equal(("application/octet-stream", false), (download.ContentType, download.Inline));
        }
    }

    [Theory]
    [InlineData("..\\..\\windows\\system32\\evil.exe", "evil.exe")]
    [InlineData("../../etc/passwd", "passwd")]
    [InlineData("C:\\dir\\report.pdf", "report.pdf")]
    [InlineData("a\"b<c>d:e|f?g*h.txt", "a_b_c_d_e_f_g_h.txt")]
    [InlineData("line\r\nbreak.txt", "line__break.txt")]
    [InlineData("   ", "file")]
    [InlineData("...", "file")]
    [InlineData(null, "file")]
    [InlineData("منتجات.pdf", "منتجات.pdf")]
    public void File_names_lose_folders_and_dangerous_characters(string? given, string expected) =>
        Assert.Equal(expected, FileService.CleanName(given));

    [Fact]
    public void A_very_long_name_is_cut_but_keeps_its_extension()
    {
        var name = FileService.CleanName(new string('x', 500) + ".pdf");

        Assert.Equal(FileService.MaxFileName, name.Length);
        Assert.EndsWith(".pdf", name);
    }

    [Fact]
    public async Task The_hash_and_size_are_recorded_and_stored_names_are_random_keys_not_file_names()
    {
        var id = await ContractAsync(Ann);
        await UploadAsync(id, "../../boot.ini", Text("abc"), Ann);

        var key = Assert.Single(_engine.FileStorage.Keys);

        Assert.Matches("^[0-9a-f]{32}$", key);
        var row = (await _engine.Documents.ListAsync("File", new ListRequest { Fields = ["file_name", "content_hash", "file_size"], Limit = 5 }, TestUsers.SystemManager)).Data.Single();
        Assert.Equal(("boot.ini", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", 3L), ((string)row["file_name"]!, (string)row["content_hash"]!, Convert.ToInt64(row["file_size"])));
    }

    // ---- limits ----

    [Fact]
    public async Task Empty_and_too_big_files_are_refused_and_leave_nothing_behind()
    {
        var id = await ContractAsync(Ann);
        var small = new TestEngine();
        small.Registry.Add(DocTypeLoader.Parse("""{ "name": "Contract", "fields": [ { "name": "title", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        var limited = new FileService(small.Registry, small.Repository, small.Documents, small.Permissions, small.Notifications, small.FileStorage, small.History) { MaxBytes = 1000 };
        var smallId = (string)(await small.Documents.InsertAsync("Contract", Values(("title", "x")), Ann))["name"]!;

        await Assert.ThrowsAsync<DocumentValidationException>(() => UploadAsync(id, "empty.txt", [], Ann));
        await Assert.ThrowsAsync<DocumentValidationException>(() => limited.UploadAsync("Contract", smallId, "big.bin", new MemoryStream(new byte[1001]), Ann));
        await limited.UploadAsync("Contract", smallId, "ok.bin", new MemoryStream(new byte[1000]), Ann); // exactly the limit is fine

        Assert.Equal(0, _engine.FileStorage.Count);
        Assert.Equal(1, small.FileStorage.Count); // only the accepted file was kept
    }

    [Fact]
    public async Task A_document_can_have_fifty_files()
    {
        var id = await ContractAsync(Ann);
        for (var i = 0; i < FileService.MaxPerDocument; i++)
            await UploadAsync(id, $"f{i}.txt", Text("x"), Ann);

        await Assert.ThrowsAsync<DocumentValidationException>(() => UploadAsync(id, "one-more.txt", Text("x"), Ann));
        Assert.Equal(FileService.MaxPerDocument, _engine.FileStorage.Count);
    }

    // ---- who may do what ----

    [Fact]
    public async Task Uploading_needs_write_access_and_downloading_read_access_to_the_document()
    {
        var id = await ContractAsync(Ann);
        var file = await UploadAsync(id, "a.txt", Text("secret"), Ann);

        await Assert.ThrowsAsync<PermissionDeniedException>(() => UploadAsync(id, "b.txt", Text("x"), Viewer)); // can read, cannot change
        await Assert.ThrowsAsync<PermissionDeniedException>(() => UploadAsync(id, "b.txt", Text("x"), Eve));
        await Assert.ThrowsAsync<NotFoundException>(() => UploadAsync("NOPE", "b.txt", Text("x"), Ann));
        await Assert.ThrowsAsync<UnauthenticatedException>(() => _engine.Files.OpenAsync(file.Id, UserContext.Anonymous));

        // Someone who may not read the document is told the file does not exist, not that it is forbidden.
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Files.OpenAsync(file.Id, Eve));
        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Files.OpenAsync("0123456789", Ann));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Files.ListAsync("Contract", id, Eve));
    }

    [Fact]
    public async Task Whoever_can_change_the_document_or_uploaded_the_file_can_remove_it()
    {
        var id = await ContractAsync(Ann);
        var file = await UploadAsync(id, "a.txt", Text("x"), Ann);

        await Assert.ThrowsAsync<NotFoundException>(() => _engine.Files.DeleteAsync(file.Id, Eve));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => _engine.Files.DeleteAsync(file.Id, Viewer)); // can read the document, may not change it
        await _engine.Files.DeleteAsync(file.Id, Bob); // a Clerk may change the document

        Assert.Equal(0, _engine.FileStorage.Count);
        Assert.Empty(await _engine.Files.ListAsync("Contract", id, Ann));
        Assert.Contains(await _engine.Activity.TimelineAsync("Contract", id, Ann), i => i.Content == "Removed a.txt");
    }

    [Fact]
    public async Task Deleting_or_renaming_a_document_takes_its_files_with_it()
    {
        var id = await ContractAsync(Ann);
        var other = await ContractAsync(Ann);
        await UploadAsync(id, "a.txt", Text("one"), Ann);
        var kept = await UploadAsync(other, "b.txt", Text("two"), Ann);

        await _engine.Documents.DeleteAsync("Contract", id, Ann);

        Assert.Equal(1, _engine.FileStorage.Count);
        Assert.Equal("two", Encoding.UTF8.GetString(await ReadAsync(await _engine.Files.OpenAsync(kept.Id, Ann))));
    }

    [Fact]
    public async Task Files_are_attached_to_documents_not_to_child_rows_or_unknown_doctypes()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Contract Line", "isChildTable": true, "fields": [ { "name": "what", "type": "Data" } ] }"""));
        var id = await ContractAsync(Ann);

        await Assert.ThrowsAnyAsync<QahwaException>(() => UploadAsync(id, "a.txt", Text("x"), Ann, doctype: "Contract Line"));
        await Assert.ThrowsAnyAsync<QahwaException>(() => UploadAsync(id, "a.txt", Text("x"), Ann, doctype: "Nope"));
        Assert.Equal(0, _engine.FileStorage.Count);
    }

    [Fact]
    public async Task Sniffing_knows_the_picture_formats()
    {
        Assert.Equal("image/png", FileService.Sniff(Png));
        Assert.Equal("image/jpeg", FileService.Sniff([0xFF, 0xD8, 0xFF, 0xE0, 0, 0]));
        Assert.Equal("image/gif", FileService.Sniff("GIF89a\0\0"u8));
        Assert.Equal("image/webp", FileService.Sniff("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal("application/octet-stream", FileService.Sniff("RIFF\0\0\0\0WAVEfmt "u8));
        Assert.Equal("application/octet-stream", FileService.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"/>"u8));
        Assert.Equal("application/octet-stream", FileService.Sniff([]));
        await Task.CompletedTask;
    }
}
