using Qahwa.Abstractions;
using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class AttachFieldTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];
    private static readonly byte[] Text = "hello"u8.ToArray();

    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public AttachFieldTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Claim", "fields": [ { "name": "title", "type": "Data" }, { "name": "receipt", "type": "Attach" }, { "name": "photo", "type": "AttachImage" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true, "delete": true } ] }
            """));
    }

    private async Task<string> ClaimAsync(string title = "Broken screen") => (string)(await _engine.Documents.InsertAsync("Claim", Values(("title", title)), Ann))["name"]!;

    private Task<FileInfoItem> UploadAsync(string claim, string name, byte[] bytes) => _engine.Files.UploadAsync("Claim", claim, name, new MemoryStream(bytes), Ann);

    [Fact]
    public async Task A_new_document_cannot_name_a_file_because_none_can_be_attached_to_it_yet()
    {
        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Claim", Values(("title", "x"), ("receipt", "abc123")), Ann));

        Assert.Contains(error.Errors, e => e.Field == "receipt" && e.Message.Contains("Save the document first"));
    }

    [Fact]
    public async Task After_saving_a_file_attached_to_the_document_can_be_named_in_the_field()
    {
        var claim = await ClaimAsync();
        var receipt = await UploadAsync(claim, "receipt.pdf", Text);
        var photo = await UploadAsync(claim, "broken.png", Png);

        var saved = await _engine.Documents.UpdateAsync("Claim", claim, Values(("receipt", receipt.Id), ("photo", photo.Id)), Ann);

        Assert.Equal(receipt.Id, saved["receipt"]);
        Assert.Equal(photo.Id, saved["photo"]);
    }

    [Fact]
    public async Task A_field_cannot_point_at_a_missing_file_or_one_of_another_document()
    {
        var mine = await ClaimAsync("Mine");
        var other = await ClaimAsync("Other");
        var theirs = await UploadAsync(other, "secret.pdf", Text);

        var missing = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Claim", mine, Values(("receipt", "nosuchfile")), Ann));
        var foreign = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Claim", mine, Values(("receipt", theirs.Id)), Ann));

        Assert.Contains(missing.Errors, e => e.Field == "receipt");
        Assert.Contains(foreign.Errors, e => e.Message.Contains("another document"));
        Assert.Null((await _engine.Documents.GetAsync("Claim", mine, Ann))["receipt"]);
    }

    [Fact]
    public async Task An_image_field_takes_only_a_real_picture()
    {
        var claim = await ClaimAsync();
        var notAPicture = await UploadAsync(claim, "photo.png", Text); // named like a picture, is not one

        var error = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Claim", claim, Values(("photo", notAPicture.Id)), Ann));

        Assert.Contains(error.Errors, e => e.Field == "photo" && e.Message.Contains("picture"));
        await _engine.Documents.UpdateAsync("Claim", claim, Values(("receipt", notAPicture.Id)), Ann); // an Attach field takes any file
    }

    [Fact]
    public async Task Removing_the_file_empties_the_field_that_named_it()
    {
        var claim = await ClaimAsync();
        var receipt = await UploadAsync(claim, "receipt.pdf", Text);
        var photo = await UploadAsync(claim, "broken.png", Png);
        await _engine.Documents.UpdateAsync("Claim", claim, Values(("receipt", receipt.Id), ("photo", photo.Id)), Ann);

        await _engine.Files.DeleteAsync(receipt.Id, Ann);

        var saved = await _engine.Documents.GetAsync("Claim", claim, Ann);
        Assert.Null(saved["receipt"]);
        Assert.Equal(photo.Id, saved["photo"]); // the other one is untouched
    }

    [Fact]
    public async Task Hostile_values_are_just_wrong_ids()
    {
        var claim = await ClaimAsync();

        foreach (var value in new[] { "../../etc/passwd", "'; drop table \"tabFile\"; --", new string('x', 500), "<script>" })
            await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Claim", claim, Values(("receipt", value)), Ann));
    }

    [Fact]
    public void Attachment_fields_belong_on_documents_not_inside_child_tables_and_the_designer_offers_them()
    {
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "Row", "isChildTable": true, "fields": [ { "name": "file", "type": "Attach" } ] }"""));
        Assert.True(Qahwa.Core.Designer.FieldTypeCatalog.IsSupported(FieldType.Attach));
        Assert.True(Qahwa.Core.Designer.FieldTypeCatalog.IsSupported(FieldType.AttachImage));
    }

    [Fact]
    public void Printouts_say_attached_instead_of_printing_an_id()
    {
        var meta = _engine.Registry.Get("Claim");

        Assert.Equal("(attached)", PrintTemplate.Show(meta.Fields.First(f => f.Name == "receipt"), "abc123"));
        Assert.Equal("", PrintTemplate.Show(meta.Fields.First(f => f.Name == "receipt"), null));
    }
}
