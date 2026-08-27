using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Miya.Generators.Core;

internal abstract class SimpleJsonValue
{
    protected SimpleJsonValue(int position)
    {
        Position = position;
    }

    internal int Position { get; }
}

internal sealed class SimpleJsonObject : SimpleJsonValue
{
    private readonly List<SimpleJsonProperty> _properties;
    private readonly Dictionary<string, SimpleJsonValue> _values;

    internal SimpleJsonObject(int position, List<SimpleJsonProperty> properties)
        : base(position)
    {
        _properties = properties;
        _values = new Dictionary<string, SimpleJsonValue>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            _values.Add(property.Name, property.Value);
        }
    }

    internal IReadOnlyList<SimpleJsonProperty> Properties => _properties;

    internal bool TryGetValue(string name, out SimpleJsonValue value) =>
        _values.TryGetValue(name, out value!);
}

internal sealed class SimpleJsonProperty
{
    internal SimpleJsonProperty(string name, int position, SimpleJsonValue value)
    {
        Name = name;
        Position = position;
        Value = value;
    }

    internal string Name { get; }

    internal int Position { get; }

    internal SimpleJsonValue Value { get; }
}

internal sealed class SimpleJsonArray : SimpleJsonValue
{
    internal SimpleJsonArray(int position, List<SimpleJsonValue> items)
        : base(position)
    {
        Items = items;
    }

    internal IReadOnlyList<SimpleJsonValue> Items { get; }
}

internal sealed class SimpleJsonString : SimpleJsonValue
{
    internal SimpleJsonString(int position, string value)
        : base(position)
    {
        Value = value;
    }

    internal string Value { get; }
}

internal sealed class SimpleJsonNumber : SimpleJsonValue
{
    internal SimpleJsonNumber(int position, string text)
        : base(position)
    {
        Text = text;
    }

    internal string Text { get; }

    internal bool TryGetInt32(out int value) =>
        int.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    internal bool TryGetInt64(out long value) =>
        long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    internal bool TryGetSingle(out float value) =>
        float.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !float.IsNaN(value)
        && !float.IsInfinity(value);

    internal bool TryGetDouble(out double value) =>
        double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        && !double.IsNaN(value)
        && !double.IsInfinity(value);

    internal bool TryGetDecimal(out decimal value) =>
        decimal.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}

internal sealed class SimpleJsonBoolean : SimpleJsonValue
{
    internal SimpleJsonBoolean(int position, bool value)
        : base(position)
    {
        Value = value;
    }

    internal bool Value { get; }
}

internal sealed class SimpleJsonNull : SimpleJsonValue
{
    internal SimpleJsonNull(int position)
        : base(position)
    {
    }
}

internal sealed class SimpleJsonParseError
{
    internal SimpleJsonParseError(int position, string message)
    {
        Position = position;
        Message = message;
    }

    internal int Position { get; }

    internal string Message { get; }
}

internal static class SimpleJsonParser
{
    internal static bool TryParse(
        string text,
        out SimpleJsonValue? value,
        out SimpleJsonParseError? error)
    {
        if (text is null)
        {
            throw new ArgumentNullException(nameof(text));
        }

        var parser = new Parser(text);
        return parser.TryParse(out value, out error);
    }

    private sealed class Parser
    {
        private const int MaximumDepth = 256;
        private readonly string _text;
        private int _position;
        private SimpleJsonParseError? _error;

        internal Parser(string text)
        {
            _text = text;
        }

        internal bool TryParse(out SimpleJsonValue? value, out SimpleJsonParseError? error)
        {
            SkipWhitespace();
            value = ParseValue(depth: 0);
            if (value is not null)
            {
                SkipWhitespace();
                if (_position != _text.Length)
                {
                    Fail("unexpected characters after the JSON value");
                    value = null;
                }
            }

            error = _error;
            return value is not null;
        }

        private SimpleJsonValue? ParseValue(int depth)
        {
            if (depth > MaximumDepth)
            {
                return Fail("the JSON nesting depth exceeds 256");
            }

            if (_position == _text.Length)
            {
                return Fail("a JSON value was expected");
            }

            var start = _position;
            switch (_text[_position])
            {
                case '{':
                    return ParseObject(depth + 1);
                case '[':
                    return ParseArray(depth + 1);
                case '"':
                    var text = ParseString();
                    return text is null ? null : new SimpleJsonString(start, text);
                case 't':
                    return ConsumeKeyword("true") ? new SimpleJsonBoolean(start, true) : null;
                case 'f':
                    return ConsumeKeyword("false") ? new SimpleJsonBoolean(start, false) : null;
                case 'n':
                    return ConsumeKeyword("null") ? new SimpleJsonNull(start) : null;
                default:
                    return _text[_position] == '-' || IsDigit(_text[_position])
                        ? ParseNumber()
                        : Fail("an object, array, string, number, Boolean, or null was expected");
            }
        }

        private SimpleJsonObject? ParseObject(int depth)
        {
            var start = _position++;
            var properties = new List<SimpleJsonProperty>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            SkipWhitespace();
            if (TryConsume('}'))
            {
                return new SimpleJsonObject(start, properties);
            }

            while (_position < _text.Length)
            {
                if (_text[_position] != '"')
                {
                    return FailObject("an object property name was expected");
                }

                var namePosition = _position;
                var name = ParseString();
                if (name is null)
                {
                    return null;
                }

                if (!names.Add(name))
                {
                    return FailObject("object property '" + name + "' appears more than once", namePosition);
                }

                SkipWhitespace();
                if (!TryConsume(':'))
                {
                    return FailObject("':' was expected after an object property name");
                }

                SkipWhitespace();
                var value = ParseValue(depth);
                if (value is null)
                {
                    return null;
                }

                properties.Add(new SimpleJsonProperty(name, namePosition, value));
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    return new SimpleJsonObject(start, properties);
                }

                if (!TryConsume(','))
                {
                    return FailObject("',' or '}' was expected in an object");
                }

                SkipWhitespace();
            }

            return FailObject("the object is not terminated");
        }

        private SimpleJsonArray? ParseArray(int depth)
        {
            var start = _position++;
            var items = new List<SimpleJsonValue>();
            SkipWhitespace();
            if (TryConsume(']'))
            {
                return new SimpleJsonArray(start, items);
            }

            while (_position < _text.Length)
            {
                var value = ParseValue(depth);
                if (value is null)
                {
                    return null;
                }

                items.Add(value);
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    return new SimpleJsonArray(start, items);
                }

                if (!TryConsume(','))
                {
                    return FailArray("',' or ']' was expected in an array");
                }

                SkipWhitespace();
            }

            return FailArray("the array is not terminated");
        }

        private string? ParseString()
        {
            _position++;
            var builder = new StringBuilder();
            while (_position < _text.Length)
            {
                var character = _text[_position++];
                if (character == '"')
                {
                    return builder.ToString();
                }

                if (character == '\\')
                {
                    if (_position == _text.Length)
                    {
                        return FailString("a string escape is incomplete");
                    }

                    var escape = _text[_position++];
                    switch (escape)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (!TryParseEscapedCharacter(builder))
                            {
                                return null;
                            }

                            break;
                        default:
                            return FailString("the string contains an invalid escape sequence");
                    }

                    continue;
                }

                if (character < ' ')
                {
                    return FailString("the string contains an unescaped control character");
                }

                if (char.IsHighSurrogate(character))
                {
                    if (_position == _text.Length || !char.IsLowSurrogate(_text[_position]))
                    {
                        return FailString("the string contains an unmatched high surrogate");
                    }

                    builder.Append(character);
                    builder.Append(_text[_position++]);
                }
                else if (char.IsLowSurrogate(character))
                {
                    return FailString("the string contains an unmatched low surrogate");
                }
                else
                {
                    builder.Append(character);
                }
            }

            return FailString("the string is not terminated");
        }

        private bool TryParseEscapedCharacter(StringBuilder builder)
        {
            if (!TryReadHexCodeUnit(out var first))
            {
                Fail("a Unicode escape must contain four hexadecimal digits");
                return false;
            }

            var character = (char)first;
            if (char.IsHighSurrogate(character))
            {
                if (_position + 2 > _text.Length
                    || _text[_position] != '\\'
                    || _text[_position + 1] != 'u')
                {
                    Fail("an escaped high surrogate must be followed by an escaped low surrogate");
                    return false;
                }

                _position += 2;
                if (!TryReadHexCodeUnit(out var second) || !char.IsLowSurrogate((char)second))
                {
                    Fail("an escaped high surrogate must be followed by an escaped low surrogate");
                    return false;
                }

                builder.Append(character);
                builder.Append((char)second);
                return true;
            }

            if (char.IsLowSurrogate(character))
            {
                Fail("an escaped low surrogate must follow an escaped high surrogate");
                return false;
            }

            builder.Append(character);
            return true;
        }

        private bool TryReadHexCodeUnit(out int value)
        {
            value = 0;
            if (_position + 4 > _text.Length)
            {
                return false;
            }

            for (var index = 0; index < 4; index++)
            {
                var digit = HexValue(_text[_position + index]);
                if (digit < 0)
                {
                    return false;
                }

                value = (value * 16) + digit;
            }

            _position += 4;
            return true;
        }

        private SimpleJsonNumber? ParseNumber()
        {
            var start = _position;
            if (TryConsume('-') && _position == _text.Length)
            {
                return FailNumber("a digit was expected after '-'");
            }

            if (TryConsume('0'))
            {
                if (_position < _text.Length && IsDigit(_text[_position]))
                {
                    return FailNumber("a JSON number cannot contain a leading zero");
                }
            }
            else
            {
                if (_position == _text.Length || !IsDigitOneToNine(_text[_position]))
                {
                    return FailNumber("a digit was expected in the JSON number");
                }

                do
                {
                    _position++;
                }
                while (_position < _text.Length && IsDigit(_text[_position]));
            }

            if (TryConsume('.'))
            {
                if (_position == _text.Length || !IsDigit(_text[_position]))
                {
                    return FailNumber("a fractional part must contain at least one digit");
                }

                do
                {
                    _position++;
                }
                while (_position < _text.Length && IsDigit(_text[_position]));
            }

            if (_position < _text.Length && (_text[_position] == 'e' || _text[_position] == 'E'))
            {
                _position++;
                if (_position < _text.Length && (_text[_position] == '+' || _text[_position] == '-'))
                {
                    _position++;
                }

                if (_position == _text.Length || !IsDigit(_text[_position]))
                {
                    return FailNumber("an exponent must contain at least one digit");
                }

                do
                {
                    _position++;
                }
                while (_position < _text.Length && IsDigit(_text[_position]));
            }

            return new SimpleJsonNumber(start, _text.Substring(start, _position - start));
        }

        private bool ConsumeKeyword(string keyword)
        {
            if (_position + keyword.Length <= _text.Length
                && string.CompareOrdinal(_text, _position, keyword, 0, keyword.Length) == 0)
            {
                _position += keyword.Length;
                return true;
            }

            Fail("the token is not valid JSON");
            return false;
        }

        private void SkipWhitespace()
        {
            while (_position < _text.Length)
            {
                var character = _text[_position];
                if (_position == 0 && character == '\uFEFF')
                {
                    _position++;
                    continue;
                }

                if (character is not (' ' or '\t' or '\r' or '\n'))
                {
                    return;
                }

                _position++;
            }
        }

        private bool TryConsume(char character)
        {
            if (_position < _text.Length && _text[_position] == character)
            {
                _position++;
                return true;
            }

            return false;
        }

        private T? Fail<T>(string message, int? position = null)
            where T : class
        {
            Fail(message, position);
            return null;
        }

        private SimpleJsonValue? Fail(string message)
        {
            Fail(message, position: null);
            return null;
        }

        private void Fail(string message, int? position)
        {
            _error ??= new SimpleJsonParseError(position ?? _position, message);
        }

        private SimpleJsonObject? FailObject(string message, int? position = null) =>
            Fail<SimpleJsonObject>(message, position);

        private SimpleJsonArray? FailArray(string message) => Fail<SimpleJsonArray>(message);

        private string? FailString(string message)
        {
            Fail(message, position: null);
            return null;
        }

        private SimpleJsonNumber? FailNumber(string message) => Fail<SimpleJsonNumber>(message);

        private static bool IsDigit(char character) => character >= '0' && character <= '9';

        private static bool IsDigitOneToNine(char character) => character >= '1' && character <= '9';

        private static int HexValue(char character)
        {
            if (character >= '0' && character <= '9')
            {
                return character - '0';
            }

            if (character >= 'a' && character <= 'f')
            {
                return character - 'a' + 10;
            }

            return character >= 'A' && character <= 'F' ? character - 'A' + 10 : -1;
        }
    }
}
