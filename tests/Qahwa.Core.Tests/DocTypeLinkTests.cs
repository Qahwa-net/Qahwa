using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class DocTypeLinkTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public DocTypeLinkTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Visible Thing", "module": "Shop", "fields": [ { "name": "a", "type": "Data" } ], "permissions": [ { "role": "Clerk", "read": true } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Hidden Thing", "module": "Shop", "fields": [ { "name": "a", "type": "Data" } ], "permissions": [ { "role": "Boss", "read": true } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Pointer", "fields": [ { "name": "kind", "type": "Link", "options": "DocType" }, { "name": "target", "type": "DynamicLink", "options": "kind" } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
    }

    [Fact]
    public async Task The_picker_offers_only_doctypes_the_person_may_read()
    {
        var found = await _engine.Documents.SearchLinkAsync("DocType", "thing", 20, Ann);

        Assert.Equal(["Visible Thing"], found.Select(o => o.Value));
        Assert.Equal("Shop", found[0].Description);
        Assert.Contains("Hidden Thing", (await _engine.Documents.SearchLinkAsync("DocType", "thing", 20, new UserContext("b@example.com", ["Boss"]))).Select(o => o.Value));
        Assert.Empty(await _engine.Documents.SearchLinkAsync("DocType", "nothing-like-this", 20, Ann));
    }

    [Fact]
    public async Task Titles_are_given_only_for_readable_doctypes()
    {
        var titles = await _engine.Documents.LinkTitlesAsync("DocType", ["Visible Thing", "Hidden Thing", "Nope"], Ann);

        Assert.Equal(["Visible Thing"], titles.Keys);
    }

    [Fact]
    public async Task A_field_can_only_hold_the_name_of_a_real_doctype()
    {
        await _engine.Documents.InsertAsync("Pointer", Values(("kind", "Visible Thing"), ("target", "x")), Ann);

        var bad = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Pointer", Values(("kind", "No Such Thing")), Ann));
        Assert.Contains(bad.Errors, e => e.Field == "kind");
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Pointer", Values(("kind", "'; drop table x; --")), Ann));
    }

    [Fact]
    public void A_dynamic_link_must_say_which_field_holds_its_doctype()
    {
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "Loose", "fields": [ { "name": "t", "type": "DynamicLink" } ] }"""));
    }

    [Fact]
    public void Core_reference_fields_are_pickers_not_free_text()
    {
        foreach (var (doctype, field) in new[] { ("Auto Repeat", "reference_doctype"), ("Comment", "reference_doctype"), ("ToDo", "reference_doctype"), ("File", "reference_doctype"),
                     ("Web Form", "doc_type"), ("Print Format", "doc_type"), ("Document Action", "target_doctype"), ("Email Template", "reference_doctype") })
        {
            var meta = _engine.Registry.Get(doctype).Fields.Single(f => f.Name == field);
            Assert.Equal(Qahwa.Abstractions.FieldType.Link, meta.Type);
            Assert.Equal("DocType", meta.Options);
        }

        foreach (var doctype in new[] { "Auto Repeat", "Comment", "ToDo", "File", "Email Queue", "Webhook Delivery" })
        {
            var meta = _engine.Registry.Get(doctype).Fields.Single(f => f.Name == "reference_name");
            Assert.Equal(Qahwa.Abstractions.FieldType.DynamicLink, meta.Type);
            Assert.Equal("reference_doctype", meta.Options);
        }
    }
}
