using System.Globalization;
using System.Text;

namespace Qahwa.SourceGenerators;

internal static class JsonObjectExtensions
{
    /// <summary>The value of a key, or null when the key is missing (Dictionary.GetValueOrDefault does not exist on netstandard2.0).</summary>
    public static object? GetValueOrDefault(this Dictionary<string, object?> json, string key) =>
        json.TryGetValue(key, out var value) ? value : null;
}

/// <summary>A small JSON reader (objects become dictionaries, arrays lists), so the generator needs no package that would have to ship beside it.</summary>
internal sealed class MiniJson
{
    private readonly string _text;
    private int _pos;

    private MiniJson(string text) => _text = text;

    public static object? Parse(string text)
    {
        var reader = new MiniJson(text.TrimStart('\uFEFF'));
        var value = reader.ReadValue();
        reader.SkipSpace();
        if (reader._pos != reader._text.Length)
            throw new FormatException("Unexpected text after the JSON value.");
        return value;
    }

    private void SkipSpace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos]))
            _pos++;
    }

    private object? ReadValue()
    {
        SkipSpace();
        if (_pos >= _text.Length)
            throw new FormatException("The JSON ends too early.");
        switch (_text[_pos])
        {
            case '{': return ReadObject();
            case '[': return ReadArray();
            case '"': return ReadString();
            case 't': Expect("true"); return true;
            case 'f': Expect("false"); return false;
            case 'n': Expect("null"); return null;
            default: return ReadNumber();
        }
    }

    private void Expect(string word)
    {
        if (string.CompareOrdinal(_text, _pos, word, 0, word.Length) != 0)
            throw new FormatException($"Unexpected text at position {_pos}.");
        _pos += word.Length;
    }

    private Dictionary<string, object?> ReadObject()
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        _pos++;
        SkipSpace();
        if (_text[_pos] == '}')
        {
            _pos++;
            return result;
        }

        while (true)
        {
            SkipSpace();
            var key = ReadString();
            SkipSpace();
            if (_text[_pos++] != ':')
                throw new FormatException($"A colon is missing at position {_pos - 1}.");
            result[key] = ReadValue();
            SkipSpace();
            var next = _text[_pos++];
            if (next == '}')
                return result;
            if (next != ',')
                throw new FormatException($"A comma or closing brace is missing at position {_pos - 1}.");
        }
    }

    private List<object?> ReadArray()
    {
        var result = new List<object?>();
        _pos++;
        SkipSpace();
        if (_text[_pos] == ']')
        {
            _pos++;
            return result;
        }

        while (true)
        {
            result.Add(ReadValue());
            SkipSpace();
            var next = _text[_pos++];
            if (next == ']')
                return result;
            if (next != ',')
                throw new FormatException($"A comma or closing bracket is missing at position {_pos - 1}.");
        }
    }

    private string ReadString()
    {
        if (_text[_pos] != '"')
            throw new FormatException($"A string was expected at position {_pos}.");
        _pos++;
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length)
                throw new FormatException("A string is not closed.");
            var c = _text[_pos++];
            if (c == '"')
                return sb.ToString();
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }

            var e = _text[_pos++];
            switch (e)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'u':
                    sb.Append((char)int.Parse(_text.Substring(_pos, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    _pos += 4;
                    break;
                default: sb.Append(e); break;
            }
        }
    }

    private object ReadNumber()
    {
        var start = _pos;
        while (_pos < _text.Length && "+-0123456789.eE".IndexOf(_text[_pos]) >= 0)
            _pos++;
        if (start == _pos)
            throw new FormatException($"Unexpected character at position {_pos}.");
        return double.Parse(_text.Substring(start, _pos - start), CultureInfo.InvariantCulture);
    }
}
