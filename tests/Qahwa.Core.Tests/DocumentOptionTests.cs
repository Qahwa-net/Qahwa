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
}
