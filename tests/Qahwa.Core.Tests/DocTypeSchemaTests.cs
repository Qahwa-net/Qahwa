using System.Text.Json;
using System.Text.Json.Nodes;

namespace Qahwa.Core.Tests;

/// <summary>The JSON Schema of a DocType file: kept in step with the model, and every DocType file we ship obeys it.</summary>
public class DocTypeSchemaTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    private static readonly string SchemaPath = Path.Combine(Root, "docs", "schema", "doctype.schema.json");

    [Fact]
    public void The_committed_schema_is_what_the_model_produces()
    {
        var expected = DocTypeSchema.Generate();
        var actual = File.ReadAllText(SchemaPath);
        Assert.True(expected == actual,
            "docs/schema/doctype.schema.json is out of date. Regenerate it: dotnet run --project src/Qahwa.Cli -- schema > docs/schema/doctype.schema.json");
    }

    [Fact]
    public void The_schema_describes_every_field_property_with_help_text()
    {
        var schema = JsonNode.Parse(DocTypeSchema.Generate())!;
        var field = schema["properties"]!["fields"]!["items"]!;
        Assert.Contains("name", field["required"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Contains("Currency", field["properties"]!["type"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(field["additionalProperties"]!.GetValue<bool>()); // a typo is flagged
        foreach (var (name, property) in field["properties"]!.AsObject())
            Assert.True(property!["description"] is not null || name is "name" or "type" or "label" or "options" or "required" or "unique" or "indexed" or "inListView" or "default" or "lowercase" or "description",
                $"The field property '{name}' has no description (add an XML comment to it).");
    }

    private static List<string> Problems(JsonElement value, JsonNode schema, string path)
    {
        var problems = new List<string>();
        var type = schema["type"]?.GetValue<string>();
        bool Is(JsonValueKind kind) => value.ValueKind == kind;
        switch (type)
        {
            case "string" when !Is(JsonValueKind.String): problems.Add($"{path}: expected text"); return problems;
            case "boolean" when !(Is(JsonValueKind.True) || Is(JsonValueKind.False)): problems.Add($"{path}: expected true or false"); return problems;
            case "integer" when !(Is(JsonValueKind.Number) && value.TryGetInt64(out _)): problems.Add($"{path}: expected a whole number"); return problems;
            case "array" when !Is(JsonValueKind.Array): problems.Add($"{path}: expected a list"); return problems;
            case "object" when !Is(JsonValueKind.Object): problems.Add($"{path}: expected an object"); return problems;
        }

        if (schema["enum"] is JsonArray choices && !choices.Any(c => c!.GetValue<string>() == value.GetString()))
            problems.Add($"{path}: '{value.GetString()}' is not one of {string.Join(", ", choices.Take(5).Select(c => c!.GetValue<string>()))}...");
        if (type == "array")
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                problems.AddRange(Problems(item, schema["items"]!, $"{path}[{index++}]"));
        }

        if (type == "object")
        {
            var properties = schema["properties"]!.AsObject();
            foreach (var property in value.EnumerateObject())
            {
                if (properties[property.Name] is { } inner)
                    problems.AddRange(Problems(property.Value, inner, $"{path}.{property.Name}"));
                else if (schema["additionalProperties"]?.GetValue<bool>() == false && property.Name != "$schema")
                    problems.Add($"{path}: unknown property '{property.Name}'");
            }

            foreach (var required in schema["required"]?.AsArray() ?? [])
                if (!value.TryGetProperty(required!.GetValue<string>(), out _))
                    problems.Add($"{path}: '{required}' is required");
        }

        return problems;
    }

    public static IEnumerable<object[]> ShippedFiles() =>
        Directory.GetFiles(Path.Combine(Root, "src", "Qahwa.Core", "CoreDocTypes"), "*.json")
            .Concat(Directory.GetFiles(Path.Combine(Root, "samples", "doctypes"), "*.json"))
            .Select(f => new object[] { Path.GetRelativePath(Root, f) });

    [Theory]
    [MemberData(nameof(ShippedFiles))]
    public void Every_DocType_file_we_ship_follows_the_schema(string relativePath)
    {
        var schema = JsonNode.Parse(DocTypeSchema.Generate())!;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, relativePath)), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        Assert.Empty(Problems(document.RootElement, schema, relativePath));
    }

    [Fact]
    public void The_validator_notices_mistakes_in_a_file()
    {
        var schema = JsonNode.Parse(DocTypeSchema.Generate())!;
        using var document = JsonDocument.Parse("""{ "nmae": "X", "fields": [ { "name": "a", "type": "Dta", "inListView": "yes" } ], "permissions": [ { "read": true } ] }""");
        var problems = Problems(document.RootElement, schema, "x");
        Assert.Contains(problems, p => p.Contains("unknown property 'nmae'"));
        Assert.Contains(problems, p => p.Contains("'name' is required"));
        Assert.Contains(problems, p => p.Contains("'Dta' is not one of"));
        Assert.Contains(problems, p => p.Contains("inListView: expected true or false"));
        Assert.Contains(problems, p => p.Contains("'role' is required"));
    }
}
