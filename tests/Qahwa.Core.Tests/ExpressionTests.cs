using System.Text.Json;
using Qahwa.Core.Expressions;

namespace Qahwa.Core.Tests;

public class ExpressionTests
{
    private static readonly string VectorsPath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "expression-vectors", "vectors.json"));

    /// <summary>JSON into the expression value model (the same shape the TypeScript engine uses).</summary>
    private static object? ToValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => element.EnumerateArray().Select(ToValue).ToList(),
        _ => element.EnumerateObject().ToDictionary(p => p.Name, p => ToValue(p.Value), StringComparer.Ordinal),
    };

    private static bool DeepEqual(object? a, object? b) => (a, b) switch
    {
        (IReadOnlyList<object?> x, IReadOnlyList<object?> y) => x.Count == y.Count && x.Zip(y).All(p => DeepEqual(p.First, p.Second)),
        (IReadOnlyDictionary<string, object?> x, IReadOnlyDictionary<string, object?> y) =>
            x.Count == y.Count && x.All(kv => y.TryGetValue(kv.Key, out var other) && DeepEqual(kv.Value, other)),
        _ => Values.AreEqual(a, b),
    };

    private static ExpressionContext Context(JsonElement env, JsonElement test)
    {
        JsonElement Pick(string name) => test.TryGetProperty(name, out var own) ? own : env.TryGetProperty(name, out var shared) ? shared : default;

        var fields = test.TryGetProperty("fields", out var f) ? (IReadOnlyDictionary<string, object?>)ToValue(f)! : new Dictionary<string, object?>();
        return new ExpressionContext
        {
            Fields = fields,
            Parent = test.TryGetProperty("parent", out var p) ? (IReadOnlyDictionary<string, object?>)ToValue(p)! : null,
            Today = Pick("today").GetString()!,
            Now = Pick("now").GetString()!,
            User = Pick("user").GetString()!,
            Roles = Pick("roles").EnumerateArray().Select(r => r.GetString()!).ToList(),
            IsNew = Pick("isNew").ValueKind == JsonValueKind.True,
        };
    }

    [Fact]
    public void Every_shared_vector_gives_the_expected_result()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        var env = document.RootElement.GetProperty("env");
        var failures = new List<string>();
        var count = 0;

        foreach (var test in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            count++;
            var source = test.GetProperty("expr").GetString()!;
            var expectsError = test.TryGetProperty("error", out var e) && e.GetBoolean();

            try
            {
                var actual = Expression.Parse(source).Evaluate(Context(env, test));
                if (expectsError)
                    failures.Add($"[{source}] should not parse but gave {actual}");
                else if (!DeepEqual(actual, ToValue(test.GetProperty("result"))))
                    failures.Add($"[{source}] expected {test.GetProperty("result")} but got {actual ?? "null"}");
            }
            catch (ExpressionException ex)
            {
                if (!expectsError)
                    failures.Add($"[{source}] failed to parse: {ex.Message}");
            }
        }

        Assert.True(count > 200, "the vector file should be loaded");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void Parse_errors_say_what_and_where()
    {
        var ex = Assert.Throws<ExpressionException>(() => Expression.Parse("qty * (rate + "));
        Assert.Contains("end", ex.Message);
        Assert.Equal(14, ex.Position);

        Assert.Contains("Unknown function 'nosuch'", Assert.Throws<ExpressionException>(() => Expression.Parse("nosuch(1)")).Message);
        Assert.Contains("takes 1 to 2", Assert.Throws<ExpressionException>(() => Expression.Parse("round()")).Message);
        Assert.Equal(6, Assert.Throws<ExpressionException>(() => Expression.Parse("a + b $ c")).Position);
        Assert.Null(Expression.Check("a + b"));
        Assert.NotNull(Expression.Check("a +"));
    }

    [Fact]
    public void Limits_stop_runaway_expressions()
    {
        Assert.Throws<ExpressionException>(() => Expression.Parse(new string('1', 1001)));
        Assert.Throws<ExpressionException>(() => Expression.Parse(string.Join(" + ", Enumerable.Repeat("1", 300))));
        Assert.Throws<ExpressionException>(() => Expression.Parse(string.Concat(Enumerable.Repeat("!", 60)) + "1"));
        Assert.NotNull(Expression.Parse(string.Join(" + ", Enumerable.Repeat("1", 50))));
    }

    [Fact]
    public void Analysis_lists_the_fields_functions_and_parent_members_an_expression_uses()
    {
        var expression = Expression.Parse("round(sum(items, 'amount') * (1 - parent.discount) + tax, 2) > limit && parent.region == 'EU'");

        Assert.Equal(["items", "limit", "tax"], expression.Identifiers.Order(StringComparer.Ordinal));
        Assert.Equal(["discount", "region"], expression.ParentMembers.Order());
        Assert.True(expression.UsesParent);
        Assert.Contains("sum", expression.Functions);
        Assert.Contains("round", expression.Functions);
        Assert.False(Expression.Parse("a + b").UsesParent);
    }

    [Fact]
    public void Evaluation_never_throws_whatever_the_data_looks_like()
    {
        var expressions = new[]
        {
            "a + b", "a * b / c", "a < b", "sum(a, b)", "len(a) + count(b)", "addDays(a, b)", "daysBetween(a, b)",
            "round(a, b)", "a.b.c", "a in b", "contains(a, b)", "min(a, b)", "number(a) + 1", "upper(a) + lower(b)",
        };
        var shapes = new object?[]
        {
            null, true, 0d, -1d, 1e300, double.NaN, double.PositiveInfinity, "", "text", "2026-03-09",
            new List<object?> { 1d, "a", null }, new Dictionary<string, object?> { ["b"] = 1d },
        };

        foreach (var source in expressions)
        {
            var expression = Expression.Parse(source);
            foreach (var a in shapes)
            {
                foreach (var b in shapes)
                {
                    var context = new ExpressionContext
                    {
                        Fields = new Dictionary<string, object?> { ["a"] = a, ["b"] = b, ["c"] = a },
                    };
                    var _ = expression.Evaluate(context);
                    expression.IsTrue(context);
                }
            }
        }
    }

    [Fact]
    public void Garbage_input_only_ever_produces_expression_exceptions()
    {
        var random = new Random(7);
        const string alphabet = "ab1 .'\"()[]+-*/%<>=!&|?:,\\\n\tin_é9";
        for (var i = 0; i < 3000; i++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 40)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            try
            {
                Expression.Parse(text);
            }
            catch (ExpressionException)
            {
            }
        }
    }

    [Fact]
    public void Stored_values_are_converted_into_the_value_model()
    {
        Assert.Equal(12d, Values.FromStorage(12));
        Assert.Equal(1.5d, Values.FromStorage(1.5m));
        Assert.Equal(true, Values.FromStorage(true));
        Assert.Null(Values.FromStorage(string.Empty));
        Assert.Equal("2026-03-09", Values.FromStorage(new DateOnly(2026, 3, 9)));
        Assert.Equal("2026-03-09T10:30:00Z", Values.FromStorage(new DateTime(2026, 3, 9, 10, 30, 0, DateTimeKind.Utc)));
        Assert.Equal("10:30:00", Values.FromStorage(new TimeOnly(10, 30)));
        Assert.Null(Values.FromStorage(null));
    }

    [Fact]
    public void Truthiness_and_text_conversion_follow_the_documented_rules()
    {
        Assert.False(Values.Truthy(null));
        Assert.False(Values.Truthy(0d));
        Assert.False(Values.Truthy(""));
        Assert.False(Values.Truthy(new List<object?>()));
        Assert.True(Values.Truthy("0"));
        Assert.True(Values.Truthy(new Dictionary<string, object?>()));
        Assert.Equal("12", Values.ToText(12d));
        Assert.Equal("0.1", Values.ToText(0.1d));
        Assert.Equal("true", Values.ToText(true));
        Assert.Equal(string.Empty, Values.ToText(null));
    }
}
