using Qahwa.Testing;
using static Qahwa.Testing.TestEngine;

namespace Qahwa.Core.Tests;

public class FieldPropertyTests
{
    private readonly TestEngine _engine = new();

    public FieldPropertyTests()
    {
        _engine.Registry.Add(DocTypeLoader.Parse("""
            { "name": "Gadget", "fields": [
                { "name": "serial", "type": "Data", "setOnce": true },
                { "name": "qty", "type": "Int", "nonNegative": true },
                { "name": "price", "type": "Currency", "nonNegative": true },
                { "name": "note", "type": "Data", "maxLength": 5 }, { "name": "ratio", "type": "Decimal", "precision": 2 }, { "name": "whole", "type": "Float", "precision": 0 } ],
              "permissions": [ { "role": "Clerk", "read": true, "write": true, "create": true } ] }
            """));
    }

    private static readonly UserContext Ann = new("ann@example.com", ["Clerk"]);

    private async Task<string> NewAsync(params (string, object?)[] values) => (string)(await _engine.Documents.InsertAsync("Gadget", Values(values), Ann))["name"]!;

    [Fact]
    public async Task A_set_once_field_can_be_filled_in_but_not_changed()
    {
        var name = await NewAsync();
        await _engine.Documents.UpdateAsync("Gadget", name, Values(("serial", "A-1")), Ann);
        await _engine.Documents.UpdateAsync("Gadget", name, Values(("serial", "A-1"), ("qty", 2)), Ann);
        var ex = await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Gadget", name, Values(("serial", "B-2")), Ann));
        Assert.Contains(ex.Errors, e => e.Field == "serial");
        await Assert.ThrowsAsync<DocumentValidationException>(() => _engine.Documents.UpdateAsync("Gadget", name, Values(("serial", "")), Ann));
    }

    [Fact]
    public async Task Negative_numbers_are_refused_where_marked()
    {
        await NewAsync(("qty", 0), ("price", 0));
        await Assert.ThrowsAsync<DocumentValidationException>(() => NewAsync(("qty", -1)));
        await Assert.ThrowsAsync<DocumentValidationException>(() => NewAsync(("price", -0.5m)));
    }

    [Fact]
    public async Task Numbers_are_rounded_to_the_decimals_a_field_keeps()
    {
        var name = await NewAsync(("ratio", 1.005m), ("whole", 2.5));
        var back = await _engine.Documents.GetAsync("Gadget", name, Ann);
        Assert.Equal(1.01m, back["ratio"]);
        Assert.Equal(3d, back["whole"]);
        var negative = await NewAsync(("ratio", -1.005m));
        Assert.Equal(-1.01m, (await _engine.Documents.GetAsync("Gadget", negative, Ann))["ratio"]);
    }

    [Fact]
    public async Task Text_longer_than_the_limit_is_refused()
    {
        await NewAsync(("note", "12345"));
        await Assert.ThrowsAsync<DocumentValidationException>(() => NewAsync(("note", "123456")));
    }

    [Theory]
    [InlineData("""{ "name": "X1", "fields": [ { "name": "a", "type": "Data", "nonNegative": true } ] }""")]
    [InlineData("""{ "name": "X2", "fields": [ { "name": "a", "type": "Int", "maxLength": 5 } ] }""")]
    [InlineData("""{ "name": "X3", "fields": [ { "name": "a", "type": "Data", "maxLength": 0 } ] }""")]
    [InlineData("""{ "name": "X4", "fields": [ { "name": "a", "type": "Data", "maxLength": 20000 } ] }""")]
    [InlineData("""{ "name": "X6", "fields": [ { "name": "a", "type": "Text", "inStandardFilter": true } ] }""")]
    [InlineData("""{ "name": "X7", "fields": [ { "name": "a", "type": "Int", "precision": 2 } ] }""")]
    [InlineData("""{ "name": "X8", "fields": [ { "name": "a", "type": "Decimal", "precision": 10 } ] }""")]
    [InlineData("""{ "name": "Y1", "fields": [ { "name": "a", "type": "Int", "translatable": true } ] }""")]
    [InlineData("""{ "name": "Y2", "fields": [ { "name": "a", "type": "Password", "inPreview": true } ] }""")]
    [InlineData("""{ "name": "Y3", "fields": [ { "name": "a", "type": "SectionBreak", "inPreview": true } ] }""")]
    [InlineData("""{ "name": "X5", "fields": [ { "name": "a", "type": "SectionBreak", "setOnce": true } ] }""")]
    public void Meaningless_combinations_are_rejected_when_a_DocType_is_defined(string json) =>
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));

    [Fact]
    public void Translatable_and_preview_fields_are_kept_when_they_make_sense()
    {
        var meta = DocTypeLoader.Parse("""
            { "name": "Z1", "fields": [ { "name": "status", "type": "Select", "options": "Open", "translatable": true, "inPreview": true }, { "name": "amount", "type": "Currency", "inPreview": true } ] }
            """);
        Assert.True(meta.Fields[0].Translatable);
        Assert.True(meta.Fields[0].InPreview);
        Assert.True(meta.Fields[1].InPreview);
        Assert.False(meta.Fields[1].Translatable);
    }
}
