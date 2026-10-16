using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class MultiSelectTests
{
    private readonly TestEngine _engine = new();
    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    public MultiSelectTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Skill", "autoname": "field:label", "fields": [ { "name": "label", "type": "Data", "required": true } ], "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Skill Row", "isChildTable": true, "fields": [ { "name": "skill", "type": "Link", "options": "Skill", "required": true } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Candidate", "autoname": "field:who", "fields": [ { "name": "who", "type": "Data", "required": true }, { "name": "skills", "type": "Table", "options": "Skill Row", "multiSelect": true } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private Task SkillAsync(string label) => _engine.Documents.InsertAsync("Skill", Values(("label", label)), Ann);

    private static object[] Rows(params string[] skills) => skills.Select(s => (object)Values(("skill", s))).ToArray();

    [Fact]
    public async Task Several_documents_can_be_picked_once_each()
    {
        await SkillAsync("C#");
        await SkillAsync("SQL");

        var saved = await _engine.Documents.InsertAsync("Candidate", Values(("who", "ann"), ("skills", Rows("C#", "SQL"))), Ann);

        Assert.Equal(["C#", "SQL"], ((IEnumerable<Dictionary<string, object?>>)saved["skills"]!).Select(r => (string)r["skill"]!));
        var twice = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Candidate", Values(("who", "bob"), ("skills", Rows("C#", "C#"))), Ann));
        Assert.Contains(twice.Errors, e => e.Field == "skills[1].skill");
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.InsertAsync("Candidate", Values(("who", "cy"), ("skills", Rows("Nope"))), Ann));
    }

    [Fact]
    public async Task A_plain_table_may_repeat_a_value_as_before()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Plain", "autoname": "field:who", "fields": [ { "name": "who", "type": "Data" }, { "name": "skills", "type": "Table", "options": "Skill Row" } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
        await SkillAsync("C#");

        var saved = await _engine.Documents.InsertAsync("Plain", Values(("who", "p"), ("skills", Rows("C#", "C#"))), Ann);

        Assert.Equal(2, ((IEnumerable<Dictionary<string, object?>>)saved["skills"]!).Count());
    }

    [Fact]
    public void Only_a_table_whose_child_has_one_link_and_nothing_else_required_can_be_a_multi_select()
    {
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("""{ "name": "Odd", "fields": [ { "name": "x", "type": "Data", "multiSelect": true } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Two Links", "isChildTable": true, "fields": [ { "name": "a", "type": "Link", "options": "Skill" }, { "name": "b", "type": "Link", "options": "Skill" } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Needs More", "isChildTable": true, "fields": [ { "name": "a", "type": "Link", "options": "Skill" }, { "name": "note", "type": "Data", "required": true } ] }"""));
        _engine.Registry.Add(DocTypeLoader.Parse("""{ "name": "Optional Note", "isChildTable": true, "fields": [ { "name": "a", "type": "Link", "options": "Skill" }, { "name": "note", "type": "Data" } ] }"""));

        Assert.Null(DocTypeMetaExtensions.MultiSelectLink(_engine.Registry.Get("Two Links")));
        Assert.Null(DocTypeMetaExtensions.MultiSelectLink(_engine.Registry.Get("Needs More")));
        Assert.Equal("a", DocTypeMetaExtensions.MultiSelectLink(_engine.Registry.Get("Optional Note"))!.Name);
        foreach (var child in new[] { "Two Links", "Needs More" })
        {
            var parent = DocTypeLoader.Parse($$"""{ "name": "P {{child}}", "fields": [ { "name": "t", "type": "Table", "options": "{{child}}", "multiSelect": true } ] }""");
            Assert.NotEmpty(_engine.Registry.CheckReferences(parent));
        }

        Assert.Empty(_engine.Registry.CheckReferences(_engine.Registry.Get("Candidate")));
    }
}
