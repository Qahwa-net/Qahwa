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

    [Fact]
    public void Sample_customer_doctype_is_valid()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "samples", "doctypes", "customer.json");
        Assert.Equal("Customer", DocTypeLoader.LoadFile(path).Name);
    }

    [Theory]
    [InlineData("Bad Name")]
    [InlineData("x; DROP TABLE users")]
    [InlineData("1abc")]
    [InlineData("owner")]
    [InlineData("modified")]
    public void Rejects_unsafe_or_reserved_field_names(string field)
    {
        var json = $$"""{ "name": "T", "fields": [ { "name": "{{field}}", "type": "Data" } ] }""";
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Theory]
    [InlineData("T\"; DROP TABLE x; --")]
    [InlineData("")]
    [InlineData("1Thing")]
    public void Rejects_unsafe_doctype_names(string name)
    {
        var json = $$"""{ "name": "{{name.Replace("\"", "\\\"")}}", "fields": [] }""";
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

    [Theory]
    [InlineData("hash", true)]
    [InlineData("prompt", true)]
    [InlineData("field:a", true)]
    [InlineData("field:missing", false)]
    [InlineData("CUST-.#####", true)]
    [InlineData("no-hashes", false)]
    [InlineData("DROP'--.###", false)]
    public void Validates_autoname_rules(string rule, bool valid)
    {
        var json = $$"""{ "name": "T", "autoname": "{{rule}}", "fields": [ { "name": "a", "type": "Data" } ] }""";
        if (valid)
            Assert.Equal("T", DocTypeLoader.Parse(json).Name);
        else
            Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Fact]
    public void Link_and_table_fields_need_a_target()
    {
        const string json = """{ "name": "T", "fields": [ { "name": "a", "type": "Link" } ] }""";
        Assert.Throws<DocTypeValidationException>(() => DocTypeLoader.Parse(json));
    }

    [Fact]
    public void Registry_rejects_dangling_links_and_duplicates()
    {
        var registry = new MetaRegistry();
        registry.Add(DocTypeLoader.Parse("""{ "name": "A", "fields": [ { "name": "x", "type": "Link", "options": "Missing" } ] }"""));
        Assert.Throws<DocTypeValidationException>(registry.ValidateReferences);
        Assert.Throws<DocTypeValidationException>(() => registry.Add(DocTypeLoader.Parse("""{ "name": "a", "fields": [] }""")));
    }
}
