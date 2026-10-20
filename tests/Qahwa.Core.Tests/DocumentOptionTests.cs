using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DocumentOptionTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    [Fact]
    public async Task A_DocType_can_limit_how_many_files_a_document_has()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Memo", "maxAttachments": 2, "fields": [ { "name": "title", "type": "Data" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        var id = (string)(await _engine.Documents.InsertAsync("Memo", Values(("title", "a")), Ann))["name"]!;
        Task<FileInfoItem> Up(string n) => _engine.Files.UploadAsync("Memo", id, n, new MemoryStream("text"u8.ToArray()), Ann);
        await Up("1.txt");
        await Up("2.txt");
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => Up("3.txt"));
        Assert.Contains("at most 2", ex.Errors.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_options_are_carried_in_the_meta_with_safe_defaults()
    {
        var plain = DocTypeLoader.Parse("""{ "name": "P1", "fields": [ { "name": "a", "type": "Data" } ] }""");
        Assert.True(plain.AllowCopy);
        Assert.True(plain.SortDescending);
        Assert.Null(plain.SortField);
        var set = DocTypeLoader.Parse("""{ "name": "P2", "sortField": "a", "sortDescending": false, "allowCopy": false, "fields": [ { "name": "a", "type": "Data" } ] }""");
        Assert.False(set.AllowCopy);
        Assert.False(set.SortDescending);
        Assert.Equal("a", set.SortField);
    }

    [Theory]
    [InlineData("""{ "name": "B1", "sortField": "missing", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "B2", "sortField": "s", "fields": [ { "name": "s", "type": "SectionBreak" } ] }""")]
    [InlineData("""{ "name": "B3", "sortField": "name; drop table x", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "B4", "maxAttachments": 0, "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "B5", "maxAttachments": 51, "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "B6", "isChildTable": true, "maxAttachments": 3, "fields": [ { "name": "a", "type": "Data" } ] }""")]
    public void Bad_options_are_refused_when_a_DocType_is_defined(string json) =>
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));

    [Fact]
    public async Task Choosing_a_document_also_searches_the_DocType_search_fields_the_person_can_read()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Client", "titleField": "label", "searchFields": [ "city", "tax_id" ], "autoname": "hash", "fields": [
                { "name": "label", "type": "Data" }, { "name": "city", "type": "Data" }, { "name": "tax_id", "type": "Data", "permLevel": 1 } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true }, { "role": "Boss", "read": true, "write": true, "create": true },
                               { "role": "Boss", "read": true, "write": true, "permLevel": 1 } ] }
            """));
        var boss = new UserContext("boss@example.com", ["Boss"]);
        await _engine.Documents.InsertAsync("Client", Values(("label", "Acme"), ("city", "Jeddah"), ("tax_id", "TX-9981")), boss);

        Assert.Single(await _engine.Documents.SearchLinkAsync("Client", "Jedd", 10, Ann));
        Assert.Single(await _engine.Documents.SearchLinkAsync("Client", "TX-99", 10, boss));
        Assert.Empty(await _engine.Documents.SearchLinkAsync("Client", "TX-99", 10, Ann)); // a field Ann may not read is not searchable by her
        Assert.Single(await _engine.Documents.SearchLinkAsync("Client", "Acme", 10, Ann));
    }

    [Theory]
    [InlineData("""{ "name": "S1", "searchFields": [ "missing" ], "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "S2", "searchFields": [ "a" ], "fields": [ { "name": "a", "type": "Int" } ] }""")]
    [InlineData("""{ "name": "S3", "searchFields": [ "a", "a" ], "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "S4", "searchFields": [ "p" ], "fields": [ { "name": "p", "type": "Password" } ] }""")]
    [InlineData("""{ "name": "S5", "searchFields": [ "a", "b", "c", "d", "e", "f" ], "fields": [ { "name": "a", "type": "Data" }, { "name": "b", "type": "Data" }, { "name": "c", "type": "Data" }, { "name": "d", "type": "Data" }, { "name": "e", "type": "Data" }, { "name": "f", "type": "Data" } ] }""")]
    public void Bad_search_fields_are_refused(string json) =>
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));

    [Fact]
    public void A_default_view_and_an_image_field_are_kept_when_they_make_sense()
    {
        var meta = DocTypeLoader.Parse("""
            { "name": "V1", "isTree": true, "defaultView": "gallery", "imageField": "pic", "fields": [ { "name": "pic", "type": "AttachImage" } ] }
            """);
        Assert.Equal("gallery", meta.DefaultView);
        Assert.Equal("pic", meta.ImageField);
        Assert.Equal("tree", DocTypeLoader.Parse("""{ "name": "V2", "isTree": true, "defaultView": "tree", "fields": [ { "name": "a", "type": "Data" } ] }""").DefaultView);
    }

    [Theory]
    [InlineData("""{ "name": "W1", "defaultView": "tree", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "W2", "defaultView": "gallery", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "W3", "defaultView": "javascript:alert(1)", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "W4", "imageField": "a", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    [InlineData("""{ "name": "W5", "isChildTable": true, "defaultView": "board", "fields": [ { "name": "a", "type": "Data" } ] }""")]
    public void Bad_views_and_image_fields_are_refused(string json) =>
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
}
