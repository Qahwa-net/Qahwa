using Qahwa.Abstractions;
using Qahwa.Core.Expressions;

namespace Qahwa.Core;

/// <summary>Checks a Document Naming Rule when it is saved: its DocType exists, its condition and its naming rule are valid for that DocType.</summary>
public static class NamingRuleCheck
{
    public static void Check(IMetaRegistry registry, IReadOnlyDictionary<string, object?> values, List<FieldError> errors)
    {
        var doctype = FieldValueConverter.Unwrap(values.GetValueOrDefault("document_type")) as string;
        if (string.IsNullOrWhiteSpace(doctype) || !registry.TryGet(doctype, out var meta))
        {
            if (values.ContainsKey("document_type"))
                errors.Add(new FieldError("document_type", "Choose a DocType that exists."));
            return;
        }

        if (meta.IsChildTable || meta.IsSingle || meta.Name == NamingService.RuleDocType)
        {
            errors.Add(new FieldError("document_type", "Rules can only be made for DocTypes whose documents have their own names (not child tables, single DocTypes or this one)."));
            return;
        }

        var fields = meta.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        if (FieldValueConverter.Unwrap(values.GetValueOrDefault("condition")) is string { Length: > 0 } condition)
        {
            if (Expression.Check(condition) is { } problem)
                errors.Add(new FieldError("condition", $"The condition is not valid: {problem}"));
            else
                foreach (var name in Expression.Parse(condition).Identifiers.Where(i => !fields.Contains(i) && !StandardFields.Columns.ContainsKey(i)))
                    errors.Add(new FieldError("condition", $"The condition refers to a field that does not exist: {name}."));
        }

        if (FieldValueConverter.Unwrap(values.GetValueOrDefault("pattern")) is string pattern)
        {
            if (pattern is "prompt" or "hash")
                errors.Add(new FieldError("pattern", "Use a series, field:<name> or expression:<expression>."));
            else
                DocTypeLoader.CheckNamingRule(pattern, fields, (path, message) => errors.Add(new FieldError("pattern", message)), "pattern");
        }
    }
}
