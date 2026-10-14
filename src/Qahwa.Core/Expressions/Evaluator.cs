using System.Globalization;

namespace Qahwa.Core.Expressions;

/// <summary>
/// Evaluates a syntax tree. Evaluation never throws: operations on values of the wrong kind give null (arithmetic)
/// or false (comparisons), so a rule can be safely evaluated against a half-filled form.
/// </summary>
internal sealed class Evaluator(ExpressionContext context)
{
    public object? Eval(Node node)
    {
        switch (node)
        {
            case Literal literal:
                return literal.Value;
            case Ident ident:
                return Lookup(ident.Name);
            case Member member:
                return Eval(member.Target) is IReadOnlyDictionary<string, object?> map && map.TryGetValue(member.Name, out var value)
                    ? value
                    : null;
            case Unary { Op: "!" } unary:
                return !Values.Truthy(Eval(unary.Operand));
            case Unary unary:
                return Eval(unary.Operand) is double d ? -d : null;
            case Logical { Op: "&&" } logical:
                return Values.Truthy(Eval(logical.Left)) && Values.Truthy(Eval(logical.Right));
            case Logical { Op: "||" } logical:
                return Values.Truthy(Eval(logical.Left)) || Values.Truthy(Eval(logical.Right));
            case Logical logical:
                return Eval(logical.Left) ?? Eval(logical.Right);
            case Ternary ternary:
                return Values.Truthy(Eval(ternary.Condition)) ? Eval(ternary.Then) : Eval(ternary.Else);
            case ListLiteral list:
                return list.Items.Select(Eval).ToList();
            case Binary binary:
                return Binary(binary.Op, Eval(binary.Left), Eval(binary.Right));
            case Call call:
                return Invoke(call);
            default:
                return null;
        }
    }

    private object? Lookup(string name)
    {
        if (name == "parent")
            return context.Parent;
        return context.Fields.TryGetValue(name, out var value) ? value : null;
    }

    private static object? Binary(string op, object? left, object? right)
    {
        switch (op)
        {
            case "==": return Values.AreEqual(left, right);
            case "!=": return !Values.AreEqual(left, right);
            case "in": return right is IReadOnlyList<object?> list && list.Any(item => Values.AreEqual(left, item));
            case "+":
                if (left is double a && right is double b)
                    return a + b;
                if (left is string || right is string)
                    return Values.ToText(left) + Values.ToText(right);
                return null;
            case "<" or "<=" or ">" or ">=":
                var order = Compare(left, right);
                return order is not null && op switch
                {
                    "<" => order < 0,
                    "<=" => order <= 0,
                    ">" => order > 0,
                    _ => order >= 0,
                };
        }

        if (left is not double x || right is not double y)
            return null;

        return op switch
        {
            "-" => x - y,
            "*" => x * y,
            "/" => y == 0 ? null : x / y,
            "%" => y == 0 ? null : x % y,
            _ => null,
        };
    }

    /// <summary>Numbers compare as numbers and text compares ordinally; anything else does not compare.</summary>
    private static int? Compare(object? left, object? right) => (left, right) switch
    {
        (double x, double y) when !double.IsNaN(x) && !double.IsNaN(y) => x.CompareTo(y),
        (string x, string y) => Math.Sign(string.CompareOrdinal(x, y)),
        _ => null,
    };

    private object? Invoke(Call call)
    {
        var args = call.Args.Select(Eval).ToList();
        object? Arg(int i) => i < args.Count ? args[i] : null;

        switch (call.Name)
        {
            case "empty":
                return Arg(0) switch
                {
                    null => true,
                    string s => s.Length == 0,
                    IReadOnlyList<object?> list => list.Count == 0,
                    _ => false,
                };
            case "len":
                return Arg(0) switch
                {
                    string s => (double)s.Length,
                    IReadOnlyList<object?> list => (double)list.Count,
                    _ => 0d,
                };
            case "lower": return Arg(0) is string lo ? Ascii(lo, upper: false) : null;
            case "upper": return Arg(0) is string up ? Ascii(up, upper: true) : null;
            case "trim": return Arg(0) is string tr ? tr.Trim(' ', '\t', '\r', '\n') : null;
            case "contains":
                return Arg(0) switch
                {
                    string s when Arg(1) is string sub => s.Contains(sub, StringComparison.Ordinal),
                    IReadOnlyList<object?> list => list.Any(item => Values.AreEqual(item, Arg(1))),
                    _ => false,
                };
            case "startsWith": return Arg(0) is string a && Arg(1) is string b && a.StartsWith(b, StringComparison.Ordinal);
            case "endsWith": return Arg(0) is string c && Arg(1) is string d && c.EndsWith(d, StringComparison.Ordinal);
            case "round":
                if (Arg(0) is not double n)
                    return null;
                var digits = Arg(1) is double dd ? Math.Clamp((int)Math.Truncate(dd), 0, 9) : 0;
                var power = Math.Pow(10, digits);
                var scaled = Math.Floor(Math.Abs(n) * power + 0.5) / power;
                return n < 0 ? -scaled : scaled;
            case "abs": return Arg(0) is double ab ? Math.Abs(ab) : null;
            case "floor": return Arg(0) is double fl ? Math.Floor(fl) : null;
            case "ceil": return Arg(0) is double ce ? Math.Ceiling(ce) : null;
            case "min" or "max":
                if (args.Any(x => x is not double))
                    return null;
                var numbers = args.Cast<double>().ToList();
                return call.Name == "min" ? numbers.Min() : numbers.Max();
            case "sum":
                if (Arg(0) is not IReadOnlyList<object?> rows)
                    return null;
                var total = 0d;
                foreach (var row in rows)
                {
                    var item = Arg(1) is string key
                        ? (row as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault(key)
                        : row;
                    if (item is double number)
                        total += number;
                }

                return total;
            case "count": return Arg(0) is IReadOnlyList<object?> counted ? (double)counted.Count : 0d;
            case "today": return context.Today;
            case "now": return context.Now;
            case "user": return context.User;
            case "hasRole": return Arg(0) is string role && context.Roles.Contains(role, StringComparer.Ordinal);
            case "isNew": return context.IsNew;
            case "text": return Values.ToText(Arg(0));
            case "number":
                return Arg(0) switch
                {
                    double num => num,
                    string s when Builtins.NumberText().IsMatch(s) => double.Parse(s, CultureInfo.InvariantCulture),
                    _ => null,
                };
            case "addDays":
                return Builtins.ParseDate(Arg(0)) is { } start && Arg(1) is double days && double.IsFinite(days)
                    ? Builtins.FormatDate(start + (long)Math.Truncate(days))
                    : null;
            case "daysBetween":
                return Builtins.ParseDate(Arg(0)) is { } from && Builtins.ParseDate(Arg(1)) is { } to ? (double)(to - from) : null;
            default:
                return null;
        }
    }

    /// <summary>Only A-Z and a-z change case, so the result is the same in every language and culture.</summary>
    private static string Ascii(string text, bool upper)
    {
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var ch = chars[i];
            if (upper && ch is >= 'a' and <= 'z')
                chars[i] = (char)(ch - 32);
            else if (!upper && ch is >= 'A' and <= 'Z')
                chars[i] = (char)(ch + 32);
        }

        return new string(chars);
    }
}
