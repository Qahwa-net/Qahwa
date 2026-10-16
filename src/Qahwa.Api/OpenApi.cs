using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core;

namespace Qahwa.Api;

/// <summary>
/// Builds an OpenAPI 3 description of the REST API for one person: only the DocTypes they may read, only the fields they may see,
/// and only the operations their rights allow. It is generated from the live DocType definitions, so it is never out of date and
/// it is exactly as revealing as the meta endpoint the Desk already uses.
/// </summary>
public static partial class OpenApiBuilder
{
    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();

    public static string SchemaName(string doctype) => Unsafe().Replace(doctype, "_");

    private static Dictionary<string, object?> SchemaOf(DocFieldMeta field)
    {
        var schema = field.Type switch
        {
            FieldType.Int => new Dictionary<string, object?> { ["type"] = "integer" },
            FieldType.Rating => new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 5 },
            FieldType.Duration => new Dictionary<string, object?> { ["type"] = "integer", ["minimum"] = 0, ["description"] = "Seconds." },
            FieldType.Color => new Dictionary<string, object?> { ["type"] = "string", ["pattern"] = "^#[0-9a-fA-F]{6}$" },
            FieldType.Geolocation => new Dictionary<string, object?> { ["type"] = "string", ["description"] = "JSON text: {\"lat\": 24.7136, \"lng\": 46.6753}." },
            FieldType.Signature => new Dictionary<string, object?> { ["type"] = "string", ["description"] = "A PNG picture as a data: address." },
            FieldType.Float or FieldType.Decimal or FieldType.Currency or FieldType.Percent => new Dictionary<string, object?> { ["type"] = "number" },
            FieldType.Check => new Dictionary<string, object?> { ["type"] = "boolean" },
            FieldType.Date => new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date" },
            FieldType.Datetime => new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time" },
            FieldType.Time => new Dictionary<string, object?> { ["type"] = "string", ["format"] = "time" },
            FieldType.Email => new Dictionary<string, object?> { ["type"] = "string", ["format"] = "email" },
            FieldType.Url => new Dictionary<string, object?> { ["type"] = "string", ["format"] = "uri" },
            FieldType.Json => new Dictionary<string, object?>(),
            FieldType.Table => new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["$ref"] = $"#/components/schemas/{SchemaName(field.Options ?? "Row")}" } },
            FieldType.Password => new Dictionary<string, object?> { ["type"] = "string", ["writeOnly"] = true },
            _ => new Dictionary<string, object?> { ["type"] = "string" },
        };

        if (field.Type == FieldType.Select && field.Options is { Length: > 0 })
            schema["enum"] = field.Options.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Prepend(string.Empty).Distinct().ToList();
        if (field.Type == FieldType.Link && field.Options is not null)
            schema["description"] = $"Name of a {field.Options}.";
        if (field.Label is not null && !schema.ContainsKey("description"))
            schema["description"] = field.Label;
        return schema;
    }

    private static Dictionary<string, object?> Object(IEnumerable<DocFieldMeta> fields, bool forInput, bool standard)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (standard)
        {
            properties["name"] = new Dictionary<string, object?> { ["type"] = "string", ["readOnly"] = true };
            properties["owner"] = new Dictionary<string, object?> { ["type"] = "string", ["readOnly"] = true };
            properties["creation"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time", ["readOnly"] = true };
            properties["modified"] = new Dictionary<string, object?> { ["type"] = "string", ["format"] = "date-time", ["description"] = "Send the value you loaded to detect changes by someone else (409)." };
            properties["docstatus"] = new Dictionary<string, object?> { ["type"] = "integer", ["enum"] = new[] { 0, 1, 2 }, ["readOnly"] = true };
        }

        var required = new List<string>();
        foreach (var field in fields.Where(f => FieldTypes.HasColumn(f.Type) || f.Type == FieldType.Table))
        {
            if (forInput && (field.ReadOnly || field.Formula is not null || field.FetchFrom is not null))
                continue;
            properties[field.Name] = SchemaOf(field);
            if (forInput && field.Required && field.Default is null)
                required.Add(field.Name);
        }

        var schema = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = properties };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static Dictionary<string, object?> Response(string description, object? schema = null) =>
        schema is null
            ? new() { ["description"] = description }
            : new() { ["description"] = description, ["content"] = new Dictionary<string, object?> { ["application/json"] = new Dictionary<string, object?> { ["schema"] = schema } } };

    private static Dictionary<string, object?> Ref(string name) => new() { ["$ref"] = $"#/components/schemas/{name}" };

    public static Dictionary<string, object?> Build(IReadOnlyList<MetaView> views, string version)
    {
        var schemas = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Error"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["code"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["message"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["details"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "object", ["properties"] = new Dictionary<string, object?> { ["field"] = new Dictionary<string, object?> { ["type"] = "string" }, ["message"] = new Dictionary<string, object?> { ["type"] = "string" } } } },
                },
            },
        };
        var paths = new Dictionary<string, object?>(StringComparer.Ordinal);
        var errors = new Dictionary<string, object?>
        {
            ["400"] = Response("The request is not valid (see details).", Ref("Error")),
            ["401"] = Response("Not signed in.", Ref("Error")),
            ["403"] = Response("Not allowed.", Ref("Error")),
            ["404"] = Response("Not found.", Ref("Error")),
            ["409"] = Response("Changed by someone else, or a unique value is taken.", Ref("Error")),
        };

        foreach (var view in views.OrderBy(v => v.Meta.Name, StringComparer.Ordinal))
        {
            var meta = view.Meta;
            var key = SchemaName(meta.Name);
            var tag = meta.Name;
            var perms = view.Permissions;
            if (!perms.Read)
                continue;

            schemas[key] = Object(meta.Fields, forInput: false, standard: true);
            schemas[key + "Input"] = Object(meta.Fields, forInput: true, standard: false);
            foreach (var (childName, child) in view.Children)
            {
                var childKey = SchemaName(childName);
                schemas[childKey] = Object(child.Fields, forInput: false, standard: false);
                schemas[childKey + "Input"] = Object(child.Fields, forInput: true, standard: false);
            }

            var collection = $"/api/resource/{Uri.EscapeDataString(meta.Name)}";
            var item = collection + "/{name}";
            var nameParam = new Dictionary<string, object?> { ["name"] = "name", ["in"] = "path", ["required"] = true, ["schema"] = new Dictionary<string, object?> { ["type"] = "string" } };

            var list = new Dictionary<string, object?>
            {
                ["tags"] = new[] { tag }, ["summary"] = $"List {meta.Name} documents",
                ["parameters"] = new object[]
                {
                    Query("fields", "JSON list of field names to return, for example [\"name\",\"status\"]."),
                    Query("filters", "JSON list of [field, operator, value], for example [[\"status\",\"=\",\"Open\"]]."),
                    Query("order_by", "A field and optionally asc or desc, for example \"modified desc\"."),
                    Query("limit_page_length", "How many rows (1 to 1000, default 20).", "integer"),
                    Query("limit_start", "How many rows to skip.", "integer"),
                },
                ["responses"] = new Dictionary<string, object?>(errors)
                {
                    ["200"] = Response("A page of documents.", new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?> { ["data"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = Ref(key) }, ["total"] = new Dictionary<string, object?> { ["type"] = "integer" } },
                    }),
                },
            };
            var collectionOps = new Dictionary<string, object?> { ["get"] = list };
            if (perms.Create)
                collectionOps["post"] = Write($"Create a {meta.Name}", tag, key, errors, "201", $"{key}Input", null);
            paths[collection] = collectionOps;

            var itemOps = new Dictionary<string, object?>
            {
                ["get"] = new Dictionary<string, object?>
                {
                    ["tags"] = new[] { tag }, ["summary"] = $"Get a {meta.Name}", ["parameters"] = new[] { nameParam },
                    ["responses"] = new Dictionary<string, object?>(errors) { ["200"] = Response("The document.", Wrapped(key)) },
                },
            };
            if (perms.Write)
                itemOps["put"] = Write($"Change a {meta.Name}", tag, key, errors, "200", $"{key}Input", nameParam);
            if (perms.Delete)
                itemOps["delete"] = new Dictionary<string, object?>
                {
                    ["tags"] = new[] { tag }, ["summary"] = $"Delete a {meta.Name}", ["parameters"] = new[] { nameParam },
                    ["responses"] = new Dictionary<string, object?>(errors) { ["200"] = Response("Deleted.") },
                };
            paths[item] = itemOps;

            if (meta.IsSubmittable && perms.Submit)
                paths[item + "/submit"] = new Dictionary<string, object?> { ["post"] = Transition($"Submit a {meta.Name}", tag, key, errors, nameParam) };
            if (meta.IsSubmittable && perms.Cancel)
                paths[item + "/cancel"] = new Dictionary<string, object?> { ["post"] = Transition($"Cancel a {meta.Name}", tag, key, errors, nameParam) };
        }

        return new Dictionary<string, object?>
        {
            ["openapi"] = "3.0.3",
            ["info"] = new Dictionary<string, object?>
            {
                ["title"] = "Qahwa REST API",
                ["version"] = version,
                ["description"] = "The documents you may use, generated from the live DocType definitions. Authenticate with the session cookie or an API key: Authorization: Bearer qk_<id>.<secret>.",
            },
            ["paths"] = paths,
            ["components"] = new Dictionary<string, object?>
            {
                ["schemas"] = schemas,
                ["securitySchemes"] = new Dictionary<string, object?>
                {
                    ["apiKey"] = new Dictionary<string, object?> { ["type"] = "http", ["scheme"] = "bearer", ["description"] = "An API key made on the Account page." },
                    ["session"] = new Dictionary<string, object?> { ["type"] = "apiKey", ["in"] = "cookie", ["name"] = ".AspNetCore.Cookies" },
                },
            },
            ["security"] = new object[]
            {
                new Dictionary<string, object?> { ["apiKey"] = Array.Empty<string>() },
                new Dictionary<string, object?> { ["session"] = Array.Empty<string>() },
            },
        };
    }

    private static Dictionary<string, object?> Query(string name, string description, string type = "string") =>
        new() { ["name"] = name, ["in"] = "query", ["description"] = description, ["schema"] = new Dictionary<string, object?> { ["type"] = type } };

    private static Dictionary<string, object?> Wrapped(string key) =>
        new() { ["type"] = "object", ["properties"] = new Dictionary<string, object?> { ["data"] = Ref(key) } };

    private static Dictionary<string, object?> Write(string summary, string tag, string key, Dictionary<string, object?> errors, string success, string input, object? nameParam)
    {
        var operation = new Dictionary<string, object?>
        {
            ["tags"] = new[] { tag }, ["summary"] = summary,
            ["requestBody"] = new Dictionary<string, object?> { ["required"] = true, ["content"] = new Dictionary<string, object?> { ["application/json"] = new Dictionary<string, object?> { ["schema"] = Ref(input) } } },
            ["responses"] = new Dictionary<string, object?>(errors) { [success] = Response("The saved document.", Wrapped(key)) },
        };
        if (nameParam is not null)
            operation["parameters"] = new[] { nameParam };
        return operation;
    }

    private static Dictionary<string, object?> Transition(string summary, string tag, string key, Dictionary<string, object?> errors, object nameParam) => new()
    {
        ["tags"] = new[] { tag }, ["summary"] = summary, ["parameters"] = new[] { nameParam },
        ["responses"] = new Dictionary<string, object?>(errors) { ["200"] = Response("The document.", Wrapped(key)) },
    };
}
