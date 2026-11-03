using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Qahwa.Abstractions;

namespace Qahwa.Core;

/// <summary>
/// The JSON Schema of a DocType file, made from the model the loader reads (<see cref="DocTypeMeta"/> and what it holds), so it can never
/// drift from it. Editors that understand JSON Schema (VS Code and most others) use it for completion, hover help and typo warnings:
/// add <c>"$schema": "https://…/doctype.schema.json"</c> (or map the file in the editor's settings). The descriptions are the XML comments
/// of the properties. Print it with <c>qahwa schema</c>.
/// </summary>
public static partial class DocTypeSchema
{
    public const string Id = "https://qahwa.dev/schema/doctype.schema.json";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Generate()
    {
        var docs = LoadDocs();
        var schema = Describe(typeof(DocTypeMeta), docs);
        schema["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        schema["$id"] = Id;
        schema["title"] = "Qahwa DocType";
        schema["description"] = "A DocType: one kind of document, from which Qahwa makes the table, the API and the screens.";

        // Ordered so the file reads the same on every build.
        var ordered = new JsonObject();
        foreach (var key in new[] { "$schema", "$id", "title", "description", "type", "additionalProperties", "required", "properties" })
        {
            if (schema[key] is { } value)
                ordered[key] = value.DeepClone();
        }

        return JsonSerializer.Serialize(ordered, Pretty) + "\n";
    }

    private static JsonObject Describe(Type type, IReadOnlyDictionary<string, string> docs)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null && p.SetMethod is not null && p.Name != "EqualityContract")
                     .OrderBy(p => p.GetCustomAttribute<RequiredMemberAttribute>() is null).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(property.Name);
            var node = TypeSchema(property.PropertyType, docs);
            if (docs.TryGetValue($"P:{type.FullName}.{property.Name}", out var text) && text.Length > 0)
                node["description"] = text;
            properties[name] = node;
            if (property.GetCustomAttribute<RequiredMemberAttribute>() is not null)
                required.Add(name);
        }

        var schema = new JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static JsonObject TypeSchema(Type type, IReadOnlyDictionary<string, string> docs)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int) || type == typeof(long)) return new JsonObject { ["type"] = "integer" };
        if (type.IsEnum)
            return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()) };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return new JsonObject { ["type"] = "array", ["items"] = TypeSchema(type.GetGenericArguments()[0], docs) };
        return Describe(type, docs);
    }

    private static Dictionary<string, string> LoadDocs()
    {
        var path = Path.ChangeExtension(typeof(DocTypeMeta).Assembly.Location, ".xml");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(path))
            return result;
        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null)
                continue;
            result[name] = Clean(summary);
        }

        return result;
    }

    /// <summary>The text of an XML comment: references and code spans reduced to their names, whitespace squeezed.</summary>
    private static string Clean(XElement summary)
    {
        var text = new StringBuilder();
        foreach (var node in summary.Nodes())
        {
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;
                case XElement e when e.Name == "see" && e.Attribute("cref") is { } cref:
                    text.Append(cref.Value[(cref.Value.LastIndexOf('.') + 1)..]);
                    break;
                case XElement e:
                    text.Append(e.Value);
                    break;
            }
        }

        return Spaces().Replace(text.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
