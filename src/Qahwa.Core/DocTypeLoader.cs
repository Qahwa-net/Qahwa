using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Qahwa.Abstractions;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

public sealed class DocTypeValidationException(string message, IReadOnlyList<FieldError>? errors = null) : Exception(message)
{
    /// <summary>Every problem found, keyed by path (e.g. <c>fields[2].name</c>); empty for load errors.</summary>
    public IReadOnlyList<FieldError> Errors { get; } = errors ?? [];
}

/// <summary>Loads and validates DocType JSON files (the source of truth for metadata).</summary>
public static partial class DocTypeLoader
{
    public const int MaxFields = 150;
    public const int MaxPermissions = 50;

    /// <summary>Names the framework's own tables use ("tab" + name), so a DocType may not take them.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase) { "Series" };

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Identifiers end up in SQL, so they are restricted to a safe set.
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$")]
    private static partial Regex FieldNameRegex();

    // Table names are "tab" + name; keep the total under the 63-char identifier limit.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9 _-]{0,59}$")]
    private static partial Regex DocTypeNameRegex();

    [GeneratedRegex(@"^([A-Za-z0-9_\-/]|\{[A-Za-z][A-Za-z0-9_]*\})*\.#{1,10}$")]
    private static partial Regex SeriesPatternRegex();

    [GeneratedRegex(@"^([a-z][a-z0-9_]*)\.([a-z][a-z0-9_]*)$")]
    private static partial Regex FetchFromRegex();

    public const int MaxValidations = 30;

    public static DocTypeMeta Parse(string json)
    {
        DocTypeMeta? meta;
        try
        {
            meta = JsonSerializer.Deserialize<DocTypeMeta>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid DocType JSON: {ex.Message}");
        }

        if (meta is null)
            throw new DocTypeValidationException("DocType JSON is empty.");

        meta = WithTreeFields(meta);
        Validate(meta);
        return meta;
    }

    public static string Serialize(DocTypeMeta meta) => JsonSerializer.Serialize(meta, Options);

    public static string SerializeWorkflow(WorkflowDefinition workflow) => JsonSerializer.Serialize(workflow, Options);

    public static WorkflowDefinition ParseWorkflow(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WorkflowDefinition>(json, Options)
                ?? throw new DocTypeValidationException("The workflow is empty.");
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid workflow: {ex.Message}");
        }
    }

    public static string SerializeAssignmentRules(AssignmentRuleSet set) => JsonSerializer.Serialize(set, Options);

    public static AssignmentRuleSet ParseAssignmentRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AssignmentRuleSet>(json, Options)
                ?? throw new DocTypeValidationException("The assignment rules are empty.");
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid assignment rules: {ex.Message}");
        }
    }

    public static string SerializeNotificationRules(NotificationRuleSet set) => JsonSerializer.Serialize(set, Options);

    public static NotificationRuleSet ParseNotificationRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<NotificationRuleSet>(json, Options)
                ?? throw new DocTypeValidationException("The notification rules are empty.");
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid notification rules: {ex.Message}");
        }
    }

    public static string SerializeWebhooks(WebhookSet set) => JsonSerializer.Serialize(set, Options);

    public static WebhookSet ParseWebhooks(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<WebhookSet>(json, Options)
                ?? throw new DocTypeValidationException("The webhooks are empty.");
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid webhooks: {ex.Message}");
        }
    }

    public static string SerializeCustomization(Customization customization) => JsonSerializer.Serialize(customization, Options);

    public static Customization ParseCustomization(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Customization>(json, Options)
                ?? throw new DocTypeValidationException("The customisation is empty.");
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid customisation: {ex.Message}");
        }
    }

    public static string SerializeRules(IReadOnlyList<DocPermMeta> rules) => JsonSerializer.Serialize(rules, Options);

    public static IReadOnlyList<DocPermMeta> ParseRules(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<DocPermMeta>>(json, Options)
                ?? throw new DocTypeValidationException("The permission rules are empty.");
        }
        catch (JsonException ex)
        {
            throw new DocTypeValidationException($"Invalid permission rules: {ex.Message}");
        }
    }

    public static DocTypeMeta LoadFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (DocTypeValidationException ex)
        {
            throw new DocTypeValidationException($"{path}: {ex.Message}", ex.Errors);
        }
    }

    /// <exception cref="DocTypeValidationException">The first problem found (see <see cref="Check"/> for all of them).</exception>
    public static void Validate(DocTypeMeta meta)
    {
        var errors = Check(meta);
        if (errors.Count > 0)
            throw new DocTypeValidationException($"{meta.Name}: {errors[0].Message}", errors);
    }

    /// <summary>Returns every problem with a DocType definition, each keyed by the path of the offending part.</summary>
    public static IReadOnlyList<FieldError> Check(DocTypeMeta meta)
    {
        var errors = new List<FieldError>();
        void Add(string path, string message) => errors.Add(new FieldError(path, message));

        if (string.IsNullOrWhiteSpace(meta.Name) || !DocTypeNameRegex().IsMatch(meta.Name))
            Add("name", $"Invalid DocType name '{meta.Name}' (letters, digits, space, underscore, hyphen; max 60 characters).");
        else if (ReservedNames.Contains(meta.Name))
            Add("name", $"'{meta.Name}' is reserved for the framework.");

        if (meta.Module is { Length: > 60 } || meta.Module?.Any(char.IsControl) == true)
            Add("module", "Module must be at most 60 characters.");

        if (meta.Fields.Count > MaxFields)
            Add("fields", $"A DocType can have at most {MaxFields} fields.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < meta.Fields.Count; i++)
            CheckField(meta.Fields[i], $"fields[{i}]", meta.IsChildTable, seen, Add);

        // Rows of a child table are named automatically and always belong to a parent document.
        if (meta.IsChildTable && meta.Autoname is not (null or "hash"))
            Add("autoname", "Child tables are named automatically; remove the naming rule.");

        if (meta.TitleField is not null && !seen.Contains(meta.TitleField))
            Add("titleField", $"Title field '{meta.TitleField}' is not a field of this DocType.");

        CheckAutoname(meta, seen, Add);
        CheckLogic(meta, seen, Add);

        errors.AddRange(CheckPermissionRules(meta.Permissions, "permissions"));
        CheckSpecial(meta, Add);
        CheckIndicators(meta, seen, Add);
        if (meta.IsSubmittable && meta.IsChildTable)
            errors.Add(new FieldError("isSubmittable", "A child table cannot be submittable; its rows follow their document."));
        if (!meta.IsSubmittable)
        {
            for (var i = 0; i < meta.Permissions.Count; i++)
            {
                if (meta.Permissions[i].Submit || meta.Permissions[i].Cancel)
                    errors.Add(new FieldError($"permissions[{i}].submit", "Submit and cancel only apply to submittable DocTypes."));
            }

            for (var i = 0; i < meta.Fields.Count; i++)
            {
                if (meta.Fields[i].AllowOnSubmit)
                    errors.Add(new FieldError($"fields[{i}].allowOnSubmit", "Allow on submit only applies to submittable DocTypes."));
            }
        }
        return errors;
    }

    public const int MaxPermLevel = 9;

    public const int MaxIndicators = 50;

    private static void CheckIndicators(DocTypeMeta meta, HashSet<string> fieldNames, Action<string, string> add)
    {
        if (meta.StatusField is null)
        {
            if (meta.Indicators.Count > 0)
                add("indicators", "Colours need a status field.");
            return;
        }

        var field = meta.Fields.FirstOrDefault(f => f.Name == meta.StatusField);
        if (field is null || !fieldNames.Contains(field.Name))
            add("statusField", $"Status field '{meta.StatusField}' is not a field of this DocType.");
        else if (field.Type is not (FieldType.Select or FieldType.Data or FieldType.Link))
            add("statusField", "The status field must be a Select, Data or Link field.");

        if (meta.Indicators.Count > MaxIndicators)
            add("indicators", $"At most {MaxIndicators} colours are allowed.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < meta.Indicators.Count; i++)
        {
            var indicator = meta.Indicators[i];
            if (string.IsNullOrWhiteSpace(indicator.Value) || indicator.Value.Length > FieldTypes.ShortTextLength)
                add($"indicators[{i}].value", "Give the value a colour is for (up to 140 characters).");
            else if (!seen.Add(indicator.Value))
                add($"indicators[{i}].value", $"'{indicator.Value}' already has a colour.");
            if (!IndicatorMeta.Colors.Contains(indicator.Color))
                add($"indicators[{i}].color", $"Use one of: {string.Join(", ", IndicatorMeta.Colors.Order())}.");
        }
    }

    private static void CheckSpecial(DocTypeMeta meta, Action<string, string> add)
    {
        if (meta.IsChildTable && (meta.IsSingle || meta.IsTree || meta.AllowRename))
            add("isChildTable", "A child table cannot be single, a tree, or renamable.");
        if (meta.IsChildTable && meta.Fields.Any(f => f.Type == FieldType.Password))
            add("fields", "A child table cannot have a Password field; keep the secret on the parent document.");
        if (meta.IsSingle && meta.IsTree)
            add("isSingle", "A DocType cannot be both single and a tree.");
        if (meta.IsSingle && meta.AllowRename)
            add("allowRename", "A single DocType has one document, named after the DocType, so it cannot be renamed.");
        if (meta.IsSingle && meta.IsSubmittable)
            add("isSingle", "A single DocType cannot be submittable.");
        if (meta.IsSingle && meta.Autoname is not (null or "hash"))
            add("autoname", "A single DocType has one document named after the DocType; remove the naming rule.");

        if (!meta.IsTree)
            return;

        var parent = meta.Fields.FirstOrDefault(f => f.Name == DocTypeMetaExtensions.TreeParentField);
        if (parent is not { Type: FieldType.Link } || !string.Equals(parent.Options, meta.Name, StringComparison.OrdinalIgnoreCase))
            add("isTree", $"A tree needs a Link field '{DocTypeMetaExtensions.TreeParentField}' pointing at the DocType itself.");
        var group = meta.Fields.FirstOrDefault(f => f.Name == DocTypeMetaExtensions.TreeGroupField);
        if (group is not { Type: FieldType.Check })
            add("isTree", $"A tree needs a Check field '{DocTypeMetaExtensions.TreeGroupField}'.");
    }

    /// <summary>A tree DocType always has its parent and group fields; add them when the definition does not list them yet.</summary>
    public static DocTypeMeta WithTreeFields(DocTypeMeta meta)
    {
        if (!meta.IsTree || meta.IsChildTable)
            return meta;

        var fields = meta.Fields.ToList();
        if (fields.All(f => f.Name != DocTypeMetaExtensions.TreeParentField))
            fields.Add(new DocFieldMeta { Name = DocTypeMetaExtensions.TreeParentField, Type = FieldType.Link, Label = "Parent", Options = meta.Name });
        if (fields.All(f => f.Name != DocTypeMetaExtensions.TreeGroupField))
            fields.Add(new DocFieldMeta { Name = DocTypeMetaExtensions.TreeGroupField, Type = FieldType.Check, Label = "Is Group" });
        return fields.Count == meta.Fields.Count ? meta : meta with { Fields = fields };
    }

    /// <summary>Checks a list of permission rules (from a definition or from the Role Permission Manager).</summary>
    public static IReadOnlyList<FieldError> CheckPermissionRules(IReadOnlyList<DocPermMeta> rules, string path)
    {
        var errors = new List<FieldError>();
        if (rules.Count > MaxPermissions)
            errors.Add(new FieldError(path, $"At most {MaxPermissions} permission rules are allowed."));

        var seen = new HashSet<(string, int, bool)>();
        for (var i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            var at = $"{path}[{i}]";
            if (string.IsNullOrWhiteSpace(rule.Role))
                errors.Add(new FieldError($"{at}.role", "Permission rule without a role."));
            else if (rule.Role.Length > FieldTypes.ShortTextLength)
                errors.Add(new FieldError($"{at}.role", "Role name is too long."));
            else if (!seen.Add((rule.Role, rule.PermLevel, rule.IfOwner)))
                errors.Add(new FieldError($"{at}.role", $"There is already a rule for '{rule.Role}' at level {rule.PermLevel}{(rule.IfOwner ? " (own documents)" : string.Empty)}."));

            if (rule.PermLevel is < 0 or > MaxPermLevel)
                errors.Add(new FieldError($"{at}.permLevel", $"The permission level must be between 0 and {MaxPermLevel}."));
            else if (rule.PermLevel > 0 && (rule.Create || rule.Delete || rule.Submit || rule.Cancel || rule.Export || rule.Report))
                errors.Add(new FieldError($"{at}.permLevel", "Create, delete, submit, cancel, export and report only apply at level 0."));
        }

        return errors;
    }

    private static void CheckField(
        DocFieldMeta field, string path, bool inChildTable, HashSet<string> seen, Action<string, string> add)
    {
        if (field.Type == FieldType.Table)
        {
            if (inChildTable)
                add($"{path}.type", "A child table cannot contain another table.");
            if (field.Unique)
                add($"{path}.unique", "A table cannot be unique.");
            if (!string.IsNullOrEmpty(field.Default))
                add($"{path}.default", "A table has no default value.");
        }
        else if (inChildTable && field.Unique)
        {
            // A unique index on a child table would be shared by the rows of every parent document.
            add($"{path}.unique", "Fields of a child table cannot be unique.");
        }

        if (!FieldNameRegex().IsMatch(field.Name))
            add($"{path}.name", $"Invalid field name '{field.Name}' (use lowercase letters, digits and underscores, starting with a letter).");
        else if (StandardFields.IsReserved(field.Name))
            add($"{path}.name", $"'{field.Name}' is a reserved field name.");
        else if (!seen.Add(field.Name))
            add($"{path}.name", $"Duplicate field name '{field.Name}'.");

        if (field.Type is FieldType.Link or FieldType.Table && string.IsNullOrWhiteSpace(field.Options))
            add($"{path}.options", $"{field.Type} fields need a target DocType.");
        if (field.Type is FieldType.Attach or FieldType.AttachImage && inChildTable)
            add($"{path}.type", "Attachment fields cannot be inside a child table: attach the file to the document itself.");
        if (field.MultiSelect && field.Type != FieldType.Table)
            add($"{path}.multiSelect", "Only a Table field can be a multi-select.");
        if (field.Type == FieldType.DynamicLink && string.IsNullOrWhiteSpace(field.Options))
            add($"{path}.options", "Dynamic Link fields need the name of the field that holds the DocType.");

        if (field.Type == FieldType.Autocomplete)
        {
            var suggestions = field.SelectOptions().ToList();
            if (suggestions.Count > 200 || suggestions.Any(s => s.Length > FieldTypes.ShortTextLength))
                add($"{path}.options", $"Autocomplete fields take at most 200 suggestions of up to {FieldTypes.ShortTextLength} characters.");
        }

        if (field.Type == FieldType.Select)
        {
            var options = field.SelectOptions().ToList();
            if (options.Count == 0)
                add($"{path}.options", "Select fields need at least one option.");
            else if (options.Count != options.Distinct(StringComparer.Ordinal).Count())
                add($"{path}.options", "Select options must be unique.");
            else if (!string.IsNullOrEmpty(field.Default) && !field.Default.StartsWith('=') && !options.Contains(field.Default, StringComparer.Ordinal))
                add($"{path}.default", "The default must be one of the options.");
        }

        if (field.PermLevel is < 0 or > MaxPermLevel)
            add($"{path}.permLevel", $"The permission level must be between 0 and {MaxPermLevel}.");
        else if (field.PermLevel > 0 && inChildTable)
            add($"{path}.permLevel", "Fields of a child table follow the permission level of the table field; they cannot have their own.");

        if (field.Label is { Length: > 140 })
            add($"{path}.label", "Label must be at most 140 characters.");
        if (field.Description is { Length: > 500 })
            add($"{path}.description", "Description must be at most 500 characters.");
        if (field.Options is { Length: > 4000 })
            add($"{path}.options", "Options are too long.");
        if (field.Default is { Length: > 140 })
            add($"{path}.default", "Default must be at most 140 characters.");
    }

    /// <summary>Parses every expression of the DocType and checks that it only reads fields that exist.</summary>
    private static void CheckLogic(DocTypeMeta meta, HashSet<string> fieldNames, Action<string, string> add)
    {
        var names = new HashSet<string>(fieldNames, StringComparer.Ordinal);
        names.UnionWith(StandardFields.Columns.Keys);

        void Expr(string? source, string path)
        {
            if (source is null)
                return;
            if (string.IsNullOrWhiteSpace(source))
            {
                add(path, "The expression is empty.");
                return;
            }

            try
            {
                var expression = Expression.Parse(source);
                foreach (var id in expression.Identifiers.Where(id => !names.Contains(id)))
                    add(path, $"Unknown field '{id}'.");
                if (expression.UsesParent && !meta.IsChildTable)
                    add(path, "'parent' can only be used in a child table.");
            }
            catch (ExpressionException ex)
            {
                add(path, $"{ex.Message} (character {ex.Position + 1})");
            }
        }

        for (var i = 0; i < meta.Fields.Count; i++)
        {
            var field = meta.Fields[i];
            var path = $"fields[{i}]";
            Expr(field.DependsOn, $"{path}.dependsOn");
            Expr(field.MandatoryDependsOn, $"{path}.mandatoryDependsOn");
            Expr(field.ReadOnlyDependsOn, $"{path}.readOnlyDependsOn");

            if (field.Formula is not null)
            {
                Expr(field.Formula, $"{path}.formula");
                if (!FieldTypes.HasColumn(field.Type))
                    add($"{path}.formula", "Only fields that store a value can be calculated.");
                if (!string.IsNullOrEmpty(field.Default))
                    add($"{path}.default", "A calculated field has no default value.");
                if (field.FetchFrom is not null)
                    add($"{path}.fetchFrom", "A field cannot both fetch a value and be calculated.");
            }

            if (field.Default is { Length: > 1 } expressionDefault && expressionDefault.StartsWith('='))
                Expr(expressionDefault[1..], $"{path}.default");

            if (field.FetchFrom is not null)
            {
                var match = FetchFromRegex().Match(field.FetchFrom);
                if (!match.Success)
                    add($"{path}.fetchFrom", "Use the form link_field.target_field.");
                else if (!meta.Fields.Any(f => f.Name == match.Groups[1].Value && f.Type == FieldType.Link))
                    add($"{path}.fetchFrom", $"'{match.Groups[1].Value}' is not a Link field of this DocType.");
            }
            else if (field.FetchIfEmpty)
            {
                add($"{path}.fetchIfEmpty", "This only applies together with a 'fetch from'.");
            }
        }

        if (meta.Validations.Count > MaxValidations)
            add("validations", $"At most {MaxValidations} rules are allowed.");
        for (var i = 0; i < meta.Validations.Count; i++)
        {
            var rule = meta.Validations[i];
            var path = $"validations[{i}]";
            Expr(rule.Expression, $"{path}.expression");
            if (string.IsNullOrWhiteSpace(rule.Message))
                add($"{path}.message", "Say what is wrong when the rule fails.");
            else if (rule.Message.Length > 300)
                add($"{path}.message", "The message must be at most 300 characters.");
            if (rule.Field is not null && !fieldNames.Contains(rule.Field))
                add($"{path}.field", $"'{rule.Field}' is not a field of this DocType.");
        }

        CheckFormulaCycles(meta, add);
    }

    private static void CheckFormulaCycles(DocTypeMeta meta, Action<string, string> add)
    {
        var formulas = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var field in meta.Fields.Where(f => f.Formula is not null))
        {
            try
            {
                formulas[field.Name] = new HashSet<string>(Expression.Parse(field.Formula!).Identifiers, StringComparer.Ordinal);
            }
            catch (ExpressionException)
            {
                // reported separately as an invalid expression
            }
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        var reported = false;

        void Visit(string name, List<string> trail)
        {
            if (reported || state.GetValueOrDefault(name) == 2)
                return;
            if (state.GetValueOrDefault(name) == 1)
            {
                var cycle = trail.SkipWhile(n => n != name).Append(name);
                var index = meta.Fields.ToList().FindIndex(f => f.Name == name);
                add($"fields[{index}].formula", $"Circular formula: {string.Join(" → ", cycle)}.");
                reported = true;
                return;
            }

            state[name] = 1;
            trail.Add(name);
            foreach (var dependency in formulas[name].Where(formulas.ContainsKey))
                Visit(dependency, trail);
            trail.RemoveAt(trail.Count - 1);
            state[name] = 2;
        }

        foreach (var name in formulas.Keys)
            Visit(name, []);
    }

    private static void CheckAutoname(DocTypeMeta meta, HashSet<string> fields, Action<string, string> add) =>
        CheckNamingRule(meta.Autoname, fields, add, "autoname");

    /// <summary>Checks a naming rule (hash, prompt, field:, expression: or a series) against the names of the fields it may read.</summary>
    internal static void CheckNamingRule(string? rule, HashSet<string> fields, Action<string, string> add, string path)
    {
        if (rule is null or "hash" or "prompt")
            return;

        if (rule.StartsWith(NamingService.ExpressionPrefix, StringComparison.Ordinal))
        {
            var source = rule[NamingService.ExpressionPrefix.Length..];
            if (Expression.Check(source) is { } problem)
                add(path, $"The naming expression is not valid: {problem}");
            else
                foreach (var name in Expression.Parse(source).Identifiers.Where(i => !fields.Contains(i) && !StandardFields.Columns.ContainsKey(i)))
                    add(path, $"The naming expression refers to a field that does not exist: {name}.");
            return;
        }

        if (rule.StartsWith("field:", StringComparison.Ordinal))
        {
            if (!fields.Contains(rule["field:".Length..]))
                add(path, $"Naming rule '{rule}' refers to a field that does not exist.");
            return;
        }

        if (!SeriesPatternRegex().IsMatch(rule))
            add(path, $"Invalid naming rule '{rule}' (use hash, prompt, field:<name>, expression:<expression> or a series like CUST-.##### or INV-{{YYYY}}-.####).");
        else
            foreach (var token in NamingService.FieldTokens(rule).Where(t => !fields.Contains(t)))
                add(path, $"Naming rule '{rule}' refers to a field that does not exist: {token}.");
    }
}
