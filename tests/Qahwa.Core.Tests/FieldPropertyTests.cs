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
                { "name": "note", "type": "Data", "maxLength": 5 } ],
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
    [InlineData("""{ "name": "X5", "fields": [ { "name": "a", "type": "SectionBreak", "setOnce": true } ] }""")]
    public void Meaningless_combinations_are_rejected_when_a_DocType_is_defined(string json) =>
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
}
