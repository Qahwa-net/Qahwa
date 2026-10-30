using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Qahwa.Core.Expressions;

/// <summary>What an expression can see: the record's fields, the parent record (for table rows) and the session.</summary>
public sealed class ExpressionContext
{
    /// <summary>Field values in the expression value model (null, bool, double, string, list, object).</summary>
    public IReadOnlyDictionary<string, object?> Fields { get; init; } = new Dictionary<string, object?>();

    /// <summary>For a row of a child table: the document it belongs to, reachable as <c>parent</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Parent { get; init; }

    public string User { get; init; } = string.Empty;

    public IReadOnlyCollection<string> Roles { get; init; } = [];

    public bool IsNew { get; init; }

    /// <summary>Today as <c>yyyy-MM-dd</c>.</summary>
    public string Today { get; init; } = "1970-01-01";

    /// <summary>The current instant as an ISO 8601 UTC string.</summary>
    public string Now { get; init; } = "1970-01-01T00:00:00Z";

    /// <summary>
    /// For <c>lookup(link_field, 'field')</c>: the fields of the document each Link field of the record points at, by Link field name.
    /// A link with no value, a missing document or one the person may not read is simply absent.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, object?>>? Linked { get; init; }
}

/// <summary>
/// A parsed expression in the small, safe language used for field logic (conditions, formulas, defaults and rules).
/// It has no loops, no assignment and no access to anything beyond <see cref="ExpressionContext"/>, so it can be
/// written by a user in the designer and evaluated identically on the server and in the Desk.
/// </summary>
public sealed class Expression
{
    private static readonly ConcurrentDictionary<string, Expression> Cache = new(StringComparer.Ordinal);
    private const int CacheLimit = 5000;

    private readonly Node _root;

    private Expression(string source, Node root)
    {
        Source = source;
        _root = root;
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var functions = new HashSet<string>(StringComparer.Ordinal);
        var parentMembers = new HashSet<string>(StringComparer.Ordinal);
        var usesParent = false;
        Walk(root, identifiers, functions, parentMembers, ref usesParent);
        Identifiers = identifiers;
        Functions = functions;
        ParentMembers = parentMembers;
        UsesParent = usesParent;
        var lookups = new List<(string, string)>();
        CollectLookups(root, lookups);
        Lookups = lookups.Distinct().ToList();
    }

    public string Source { get; }

    /// <summary>The field names the expression reads (not counting <c>parent</c>).</summary>
    public IReadOnlySet<string> Identifiers { get; }

    /// <summary>The fields it reads from the parent document, as in <c>parent.rate</c>.</summary>
    public IReadOnlySet<string> ParentMembers { get; }

    public bool UsesParent { get; }

    public IReadOnlySet<string> Functions { get; }

    /// <summary>Every <c>lookup(link_field, 'field')</c> in the expression, as (link field, field of the linked document).</summary>
    public IReadOnlyList<(string Link, string Field)> Lookups { get; private set; } = [];

    private static void CollectLookups(Node node, List<(string, string)> found)
    {
        switch (node)
        {
            case Call { Name: "lookup", Args: [Ident link, Literal { Value: string field }] }:
                found.Add((link.Name, field));
                break;
            case Call call:
                foreach (var arg in call.Args)
                    CollectLookups(arg, found);
                break;
            case Member member: CollectLookups(member.Target, found); break;
            case Unary unary: CollectLookups(unary.Operand, found); break;
            case Binary binary: CollectLookups(binary.Left, found); CollectLookups(binary.Right, found); break;
            case Logical logical: CollectLookups(logical.Left, found); CollectLookups(logical.Right, found); break;
            case Ternary ternary: CollectLookups(ternary.Condition, found); CollectLookups(ternary.Then, found); CollectLookups(ternary.Else, found); break;
            case ListLiteral list:
                foreach (var item in list.Items)
                    CollectLookups(item, found);
                break;
        }
    }

    /// <exception cref="ExpressionException">The source is not a valid expression.</exception>
    public static Expression Parse(string source)
    {
        if (Cache.TryGetValue(source, out var cached))
            return cached;

        var expression = new Expression(source, Parser.Parse(source));
        if (Cache.Count >= CacheLimit)
            Cache.Clear();
        Cache[source] = expression;
        return expression;
    }

    /// <returns>The error message, or null when the source is valid.</returns>
    public static string? Check(string source)
    {
        try
        {
            Parse(source);
            return null;
        }
        catch (ExpressionException ex)
        {
            return ex.Message;
        }
    }

    public object? Evaluate(ExpressionContext context) => new Evaluator(context).Eval(_root);

    /// <summary>True when the result counts as "yes" (not null, false, 0, empty text or an empty list).</summary>
    public bool IsTrue(ExpressionContext context) => Values.Truthy(Evaluate(context));

    private static void Walk(
        Node node, HashSet<string> identifiers, HashSet<string> functions, HashSet<string> parentMembers, ref bool usesParent)
    {
        switch (node)
        {
            case Ident { Name: "parent" }:
                usesParent = true;
                break;
            case Ident ident:
                identifiers.Add(ident.Name);
                break;
            case Member { Target: Ident { Name: "parent" } } member:
                usesParent = true;
                parentMembers.Add(member.Name);
                break;
            case Member member:
                Walk(member.Target, identifiers, functions, parentMembers, ref usesParent);
                break;
            case Unary unary:
                Walk(unary.Operand, identifiers, functions, parentMembers, ref usesParent);
                break;
            case Binary binary:
                Walk(binary.Left, identifiers, functions, parentMembers, ref usesParent);
                Walk(binary.Right, identifiers, functions, parentMembers, ref usesParent);
                break;
            case Logical logical:
                Walk(logical.Left, identifiers, functions, parentMembers, ref usesParent);
                Walk(logical.Right, identifiers, functions, parentMembers, ref usesParent);
                break;
            case Ternary ternary:
                Walk(ternary.Condition, identifiers, functions, parentMembers, ref usesParent);
                Walk(ternary.Then, identifiers, functions, parentMembers, ref usesParent);
                Walk(ternary.Else, identifiers, functions, parentMembers, ref usesParent);
                break;
            case Call call:
                functions.Add(call.Name);
                foreach (var arg in call.Args)
                    Walk(arg, identifiers, functions, parentMembers, ref usesParent);
                break;
            case ListLiteral list:
                foreach (var item in list.Items)
                    Walk(item, identifiers, functions, parentMembers, ref usesParent);
                break;
        }
    }
}

/// <summary>The value model shared with the TypeScript implementation: null, bool, double, string, list, object.</summary>
public static class Values
{
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        double d => d != 0 && !double.IsNaN(d),
        string s => s.Length > 0,
        IReadOnlyList<object?> list => list.Count > 0,
        _ => true,
    };

    public static bool AreEqual(object? a, object? b) => (a, b) switch
    {
        (null, null) => true,
        (bool x, bool y) => x == y,
        (double x, double y) => x == y,
        (string x, string y) => string.Equals(x, y, StringComparison.Ordinal),
        _ => false,
    };

    /// <summary>How a value becomes text when joined to a string or passed to <c>text()</c>.</summary>
    public static string ToText(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        double d when d == Math.Floor(d) && Math.Abs(d) < 1e15 => ((long)d).ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        string s => s,
        _ => string.Empty,
    };

    /// <summary>Converts a stored value (int, decimal, DateOnly, ...) into the expression value model.</summary>
    public static object? FromStorage(object? value) => value switch
    {
        null => null,
        bool b => b,
        string s => s.Length == 0 ? null : s,
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal =>
            Convert.ToDouble(value, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}

internal static partial class Builtins
{
    /// <summary>Function name to (minimum, maximum) argument count.</summary>
    public static readonly Dictionary<string, (int Min, int Max)> Arity = new(StringComparer.Ordinal)
    {
        ["empty"] = (1, 1), ["len"] = (1, 1), ["lower"] = (1, 1), ["upper"] = (1, 1), ["trim"] = (1, 1),
        ["contains"] = (2, 2), ["startsWith"] = (2, 2), ["endsWith"] = (2, 2),
        ["round"] = (1, 2), ["abs"] = (1, 1), ["floor"] = (1, 1), ["ceil"] = (1, 1), ["min"] = (1, 8), ["max"] = (1, 8),
        ["sum"] = (1, 2), ["count"] = (1, 1),
        ["lookup"] = (2, 2), ["avg"] = (2, 2), ["minOf"] = (2, 2), ["maxOf"] = (2, 2), ["countIf"] = (3, 3), ["sumIf"] = (4, 4), ["distinct"] = (1, 2),
        ["today"] = (0, 0), ["now"] = (0, 0), ["user"] = (0, 0), ["hasRole"] = (1, 1), ["isNew"] = (0, 0),
        ["text"] = (1, 1), ["number"] = (1, 1),
        ["addDays"] = (2, 2), ["daysBetween"] = (2, 2),
    };

    [GeneratedRegex(@"^-?\d+(\.\d+)?$")]
    public static partial Regex NumberText();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}")]
    public static partial Regex DatePrefix();

    /// <summary>Days since 1970-01-01 for a proleptic Gregorian date (Howard Hinnant's algorithm, identical in the Desk).</summary>
    public static long DaysFromCivil(long y, long m, long d)
    {
        y -= m <= 2 ? 1 : 0;
        var era = (y >= 0 ? y : y - 399) / 400;
        var yoe = y - era * 400;
        var doy = (153 * (m + (m > 2 ? -3 : 9)) + 2) / 5 + d - 1;
        var doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
        return era * 146097 + doe - 719468;
    }

    public static (long Y, long M, long D) CivilFromDays(long z)
    {
        z += 719468;
        var era = (z >= 0 ? z : z - 146096) / 146097;
        var doe = z - era * 146097;
        var yoe = (doe - doe / 1460 + doe / 36524 - doe / 146096) / 365;
        var y = yoe + era * 400;
        var doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
        var mp = (5 * doy + 2) / 153;
        var d = doy - (153 * mp + 2) / 5 + 1;
        var m = mp + (mp < 10 ? 3 : -9);
        return (y + (m <= 2 ? 1 : 0), m, d);
    }

    public static long? ParseDate(object? value)
    {
        if (value is not string s || !DatePrefix().IsMatch(s))
            return null;

        var y = long.Parse(s.AsSpan(0, 4), CultureInfo.InvariantCulture);
        var m = long.Parse(s.AsSpan(5, 2), CultureInfo.InvariantCulture);
        var d = long.Parse(s.AsSpan(8, 2), CultureInfo.InvariantCulture);
        if (m is < 1 or > 12 || d < 1)
            return null;

        var days = DaysFromCivil(y, m, d);
        var back = CivilFromDays(days);
        return back == (y, m, d) ? days : null; // rejects 2026-02-30 and the like
    }

    public static string FormatDate(long days)
    {
        var (y, m, d) = CivilFromDays(days);
        return $"{y:0000}-{m:00}-{d:00}";
    }
}
