using System.Globalization;
using System.Text;

namespace Qahwa.Core.Expressions;

/// <summary>A problem in an expression's source text. <see cref="Position"/> is the zero-based character offset.</summary>
public sealed class ExpressionException(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}

internal abstract record Node;

internal sealed record Literal(object? Value) : Node;

internal sealed record Ident(string Name) : Node;

internal sealed record Member(Node Target, string Name) : Node;

internal sealed record Unary(string Op, Node Operand) : Node;

internal sealed record Binary(string Op, Node Left, Node Right) : Node;

internal sealed record Logical(string Op, Node Left, Node Right) : Node;

internal sealed record Ternary(Node Condition, Node Then, Node Else) : Node;

internal sealed record Call(string Name, IReadOnlyList<Node> Args) : Node;

internal sealed record ListLiteral(IReadOnlyList<Node> Items) : Node;

internal enum TokenKind { Number, String, Ident, Punct, End }

internal readonly record struct Token(TokenKind Kind, string Text, int Position, double Number = 0);

/// <summary>
/// Turns source text into a syntax tree. The same grammar is implemented in the Desk (TypeScript) and both are
/// checked against the shared vectors in tests/expression-vectors, so a rule behaves the same on both sides.
/// </summary>
internal sealed class Parser
{
    public const int MaxLength = 1000;
    private const int MaxDepth = 40;
    private const int MaxNodes = 400;

    private static readonly string[] Punctuation =
    [
        "==", "!=", "<=", ">=", "&&", "||", "??",
        "+", "-", "*", "/", "%", "<", ">", "!", "?", ":", "(", ")", "[", "]", ",", ".",
    ];

    private readonly string _source;
    private readonly List<Token> _tokens = [];
    private int _index;
    private int _depth;
    private int _nodes;

    private Parser(string source)
    {
        _source = source;
        Tokenize();
    }

    public static Node Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ExpressionException("The expression is empty.", 0);
        if (source.Length > MaxLength)
            throw new ExpressionException($"The expression is too long (max {MaxLength} characters).", MaxLength);

        var parser = new Parser(source);
        var node = parser.ParseTernary();
        if (parser.Peek.Kind != TokenKind.End)
            throw new ExpressionException($"Unexpected '{parser.Peek.Text}'.", parser.Peek.Position);
        return node;
    }

    private Token Peek => _tokens[_index];

    private Token Next() => _tokens[_index++];

    private bool IsPunct(string text) => Peek.Kind == TokenKind.Punct && Peek.Text == text;

    private bool IsKeyword(string text) => Peek.Kind == TokenKind.Ident && Peek.Text == text;

    private void Expect(string text)
    {
        if (!IsPunct(text))
            throw new ExpressionException($"Expected '{text}' but found '{Describe(Peek)}'.", Peek.Position);
        _index++;
    }

    private static string Describe(Token token) => token.Kind == TokenKind.End ? "the end" : token.Text;

    private T Count<T>(T node) where T : Node
    {
        if (++_nodes > MaxNodes)
            throw new ExpressionException("The expression is too complex.", Peek.Position);
        return node;
    }

    private Node ParseTernary()
    {
        if (++_depth > MaxDepth)
            throw new ExpressionException("The expression is nested too deeply.", Peek.Position);

        var condition = ParseCoalesce();
        Node result = condition;
        if (IsPunct("?"))
        {
            _index++;
            var then = ParseTernary();
            Expect(":");
            var otherwise = ParseTernary();
            result = Count(new Ternary(condition, then, otherwise));
        }

        _depth--;
        return result;
    }

    private Node ParseCoalesce()
    {
        var left = ParseOr();
        while (IsPunct("??"))
        {
            _index++;
            left = Count(new Logical("??", left, ParseOr()));
        }

        return left;
    }

    private Node ParseOr()
    {
        var left = ParseAnd();
        while (IsPunct("||"))
        {
            _index++;
            left = Count(new Logical("||", left, ParseAnd()));
        }

        return left;
    }

    private Node ParseAnd()
    {
        var left = ParseEquality();
        while (IsPunct("&&"))
        {
            _index++;
            left = Count(new Logical("&&", left, ParseEquality()));
        }

        return left;
    }

    private Node ParseEquality()
    {
        var left = ParseRelational();
        while (IsPunct("==") || IsPunct("!="))
        {
            var op = Next().Text;
            left = Count(new Binary(op, left, ParseRelational()));
        }

        return left;
    }

    private Node ParseRelational()
    {
        var left = ParseAdditive();
        while (true)
        {
            if (IsPunct("<") || IsPunct("<=") || IsPunct(">") || IsPunct(">="))
            {
                var op = Next().Text;
                left = Count(new Binary(op, left, ParseAdditive()));
            }
            else if (IsKeyword("in"))
            {
                _index++;
                left = Count(new Binary("in", left, ParseAdditive()));
            }
            else
            {
                return left;
            }
        }
    }

    private Node ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (IsPunct("+") || IsPunct("-"))
        {
            var op = Next().Text;
            left = Count(new Binary(op, left, ParseMultiplicative()));
        }

        return left;
    }

    private Node ParseMultiplicative()
    {
        var left = ParseUnary();
        while (IsPunct("*") || IsPunct("/") || IsPunct("%"))
        {
            var op = Next().Text;
            left = Count(new Binary(op, left, ParseUnary()));
        }

        return left;
    }

    private Node ParseUnary()
    {
        if (IsPunct("!") || IsPunct("-"))
        {
            if (++_depth > MaxDepth)
                throw new ExpressionException("The expression is nested too deeply.", Peek.Position);
            var op = Next().Text;
            var node = Count(new Unary(op, ParseUnary()));
            _depth--;
            return node;
        }

        return ParsePostfix();
    }

    private Node ParsePostfix()
    {
        var node = ParsePrimary();
        while (IsPunct("."))
        {
            _index++;
            if (Peek.Kind != TokenKind.Ident)
                throw new ExpressionException("Expected a field name after '.'.", Peek.Position);
            node = Count(new Member(node, Next().Text));
        }

        return node;
    }

    private Node ParsePrimary()
    {
        var token = Next();
        switch (token.Kind)
        {
            case TokenKind.Number:
                return Count(new Literal(token.Number));
            case TokenKind.String:
                return Count(new Literal(token.Text));
            case TokenKind.Ident:
                switch (token.Text)
                {
                    case "true": return Count(new Literal(true));
                    case "false": return Count(new Literal(false));
                    case "null": return Count(new Literal(null));
                    case "in":
                        throw new ExpressionException("Unexpected 'in'.", token.Position);
                }

                if (IsPunct("("))
                    return ParseCall(token);
                return Count(new Ident(token.Text));
            case TokenKind.Punct when token.Text == "(":
                var inner = ParseTernary();
                Expect(")");
                return inner;
            case TokenKind.Punct when token.Text == "[":
                var items = new List<Node>();
                if (!IsPunct("]"))
                {
                    do
                    {
                        items.Add(ParseTernary());
                    }
                    while (IsPunct(",") && _tokens[_index++].Kind == TokenKind.Punct);
                }

                Expect("]");
                return Count(new ListLiteral(items));
            default:
                throw new ExpressionException($"Unexpected {(token.Kind == TokenKind.End ? "end of the expression" : $"'{token.Text}'")}.", token.Position);
        }
    }

    private Node ParseCall(Token name)
    {
        if (!Builtins.Arity.TryGetValue(name.Text, out var arity))
            throw new ExpressionException($"Unknown function '{name.Text}'.", name.Position);

        Expect("(");
        var args = new List<Node>();
        if (!IsPunct(")"))
        {
            do
            {
                args.Add(ParseTernary());
            }
            while (IsPunct(",") && _tokens[_index++].Kind == TokenKind.Punct);
        }

        Expect(")");
        if (args.Count < arity.Min || args.Count > arity.Max)
        {
            var expected = arity.Min == arity.Max ? $"{arity.Min}" : $"{arity.Min} to {arity.Max}";
            throw new ExpressionException($"{name.Text}() takes {expected} argument(s), not {args.Count}.", name.Position);
        }

        // lookup(link_field, 'field') reads a field of the linked document: it names a field and a literal, never a computed value.
        if (name.Text == "lookup" && !(args[0] is Ident { Name: not "parent" } && args[1] is Literal { Value: string }))
            throw new ExpressionException("lookup() needs a Link field and the name of a field, as in lookup(customer, 'credit_limit').", name.Position);

        return Count(new Call(name.Text, args));
    }

    private void Tokenize()
    {
        var i = 0;
        while (i < _source.Length)
        {
            var c = _source[i];
            if (c is ' ' or '\t' or '\r' or '\n')
            {
                i++;
                continue;
            }

            if (c is >= '0' and <= '9')
            {
                var start = i;
                while (i < _source.Length && _source[i] is >= '0' and <= '9')
                    i++;
                if (i + 1 < _source.Length && _source[i] == '.' && _source[i + 1] is >= '0' and <= '9')
                {
                    i++;
                    while (i < _source.Length && _source[i] is >= '0' and <= '9')
                        i++;
                }

                var text = _source[start..i];
                _tokens.Add(new Token(TokenKind.Number, text, start, double.Parse(text, CultureInfo.InvariantCulture)));
                continue;
            }

            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_')
            {
                var start = i;
                while (i < _source.Length && _source[i] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '_' or >= '0' and <= '9')
                    i++;
                _tokens.Add(new Token(TokenKind.Ident, _source[start..i], start));
                continue;
            }

            if (c is '\'' or '"')
            {
                var start = i;
                var builder = new StringBuilder();
                i++;
                var closed = false;
                while (i < _source.Length)
                {
                    var ch = _source[i++];
                    if (ch == c)
                    {
                        closed = true;
                        break;
                    }

                    if (ch != '\\')
                    {
                        builder.Append(ch);
                        continue;
                    }

                    if (i >= _source.Length)
                        break;
                    var escaped = _source[i++];
                    builder.Append(escaped switch
                    {
                        '\\' => '\\',
                        '\'' => '\'',
                        '"' => '"',
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        _ => throw new ExpressionException($"Unknown escape '\\{escaped}'.", i - 2),
                    });
                }

                if (!closed)
                    throw new ExpressionException("The text is missing its closing quote.", start);
                _tokens.Add(new Token(TokenKind.String, builder.ToString(), start));
                continue;
            }

            var matched = Punctuation.FirstOrDefault(p => string.CompareOrdinal(_source, i, p, 0, p.Length) == 0);
            if (matched is null)
                throw new ExpressionException($"Unexpected character '{c}'.", i);
            _tokens.Add(new Token(TokenKind.Punct, matched, i));
            i += matched.Length;
        }

        _tokens.Add(new Token(TokenKind.End, string.Empty, _source.Length));
    }
}
