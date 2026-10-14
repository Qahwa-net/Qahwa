using Qahwa.Abstractions;

namespace Qahwa.Core.Tests;

public class DocTypeLoaderTests
{
    [Fact]
    public void Parses_valid_doctype()
    {
        var meta = DocTypeLoader.Parse("""
            { "name": "Customer", "titleField": "customer_name",
              "fields": [ { "name": "customer_name", "type": "Data", "required": true } ] }
            """);

        Assert.Equal("Customer", meta.Name);
        Assert.Equal(FieldType.Data, meta.Fields[0].Type);
        Assert.True(meta.Fields[0].Required);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("x; DROP TABLE users")]
    [InlineData("1abc")]
    public void Rejects_unsafe_field_names(string field)
    {
        var json = $$"""{ "name": "T", "fields": [ { "name": "{{field}}", "type": "Data" } ] }""";
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Fact]
    public void Rejects_duplicate_fields()
    {
        const string json = """
            { "name": "T", "fields": [ { "name": "a", "type": "Data" }, { "name": "a", "type": "Int" } ] }
            """;
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Fact]
    public void Rejects_unknown_title_field()
    {
        const string json = """{ "name": "T", "titleField": "nope", "fields": [] }""";
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Fact]
    public void Wraps_malformed_json()
    {
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse("{ not json"));
    }
}
