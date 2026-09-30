using Hypa.AgentRuntime.Domain.AttachConfig;

namespace Hypa.AgentRuntime.Infrastructure.Config;

/// <summary>
/// AOT-safe TOML subset for attach config. Line numbers on every value.
/// Supports tables, array-of-tables, dotted keys, strings, bools, ints,
/// arrays, and inline tables. No reflection. No ToModel.
/// </summary>
internal static class TomlSubsetParser
{
    public static AttachConfigResult<TomlDocument> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parser = new Parser(text);
        return parser.ParseDocument();
    }

    internal sealed class TomlDocument
    {
        public List<TomlAssignment> Assignments { get; } = [];
        public List<TomlArrayTable> ArrayTables { get; } = [];
        public Dictionary<string, int> TableHeaders { get; } = new(StringComparer.Ordinal);
    }

    internal sealed record TomlAssignment(string Path, TomlValue Value, int Line);

    internal sealed record TomlArrayTable(string Path, List<TomlAssignment> Fields, int Line);

    internal abstract record TomlValue(int Line);

    internal sealed record TomlStringValue(string Value, int Line) : TomlValue(Line);

    internal sealed record TomlBoolValue(bool Value, int Line) : TomlValue(Line);

    internal sealed record TomlIntValue(long Value, int Line) : TomlValue(Line);

    internal sealed record TomlFloatValue(double Value, int Line) : TomlValue(Line);

    internal sealed record TomlArrayValue(IReadOnlyList<TomlValue> Items, int Line) : TomlValue(Line);

    internal sealed record TomlInlineTableValue(IReadOnlyList<TomlAssignment> Fields, int Line) : TomlValue(Line);

    private sealed class Parser(string text)
    {
        private readonly string _text = text;
        private int _i;
        private int _line = 1;
        private readonly List<AttachConfigError> _errors = [];

        public AttachConfigResult<TomlDocument> ParseDocument()
        {
            var doc = new TomlDocument();
            var paths = new PathRegistry();
            var currentTable = "";
            List<TomlAssignment>? arrayFields = null;
            PathRegistry? arrayPaths = null;

            while (true)
            {
                SkipWsAndComments();
                if (AtEnd)
                    break;

                if (Peek() == '[')
                {
                    var headerLine = _line;
                    if (Peek(1) == '[')
                    {
                        if (!TryParseArrayTableName(out var name))
                            return Fail();
                        if (!paths.TryDefineArrayTable(name, out var error))
                        {
                            Error(headerLine, error);
                            return Fail();
                        }

                        doc.TableHeaders.TryAdd(name, headerLine);
                        arrayFields = [];
                        arrayPaths = new PathRegistry();
                        currentTable = name;
                        doc.ArrayTables.Add(new TomlArrayTable(name, arrayFields, headerLine));
                    }
                    else
                    {
                        if (!TryParseTableName(out var name))
                            return Fail();
                        if (!paths.TryDefineTable(name, out var error))
                        {
                            Error(headerLine, error);
                            return Fail();
                        }

                        doc.TableHeaders.TryAdd(name, headerLine);
                        arrayFields = null;
                        arrayPaths = null;
                        currentTable = name;
                    }

                    continue;
                }

                var assignLine = _line;
                if (!TryParseDottedKey(out var key))
                    return Fail();
                SkipSpaceTab();
                if (!Match('='))
                {
                    Error(assignLine, "Expected '=' after key.");
                    return Fail();
                }

                SkipSpaceTab();
                if (!TryParseValue(out var value))
                    return Fail();

                var path = string.IsNullOrEmpty(currentTable) ? key : currentTable + "." + key;
                var assignment = new TomlAssignment(path, value, assignLine);
                if (arrayFields is not null)
                {
                    if (arrayPaths is not null && !arrayPaths.TryAssignValue(key, out var arrayError))
                    {
                        Error(assignLine, arrayError);
                        return Fail();
                    }

                    arrayFields.Add(assignment);
                }
                else
                {
                    if (!paths.TryAssignValue(path, out var error))
                    {
                        Error(assignLine, error);
                        return Fail();
                    }

                    doc.Assignments.Add(assignment);
                }

                SkipSpaceTab();
                if (!AtEnd && Peek() is not ('\n' or '\r' or '#'))
                {
                    Error(_line, $"Unexpected text after value for '{path}'.");
                    return Fail();
                }
            }

            return _errors.Count > 0
                ? AttachConfigResult<TomlDocument>.Fail(_errors)
                : AttachConfigResult<TomlDocument>.Ok(doc);
        }

        private bool TryParseTableName(out string name)
        {
            name = "";
            var line = _line;
            if (!Match('['))
            {
                Error(line, "Expected '['.");
                return false;
            }

            SkipSpaceTab();
            if (!TryParseDottedKey(out name))
                return false;
            SkipSpaceTab();
            if (!Match(']'))
            {
                Error(line, "Expected ']' after table name.");
                return false;
            }

            return true;
        }

        private bool TryParseArrayTableName(out string name)
        {
            name = "";
            var line = _line;
            if (!Match('[') || !Match('['))
            {
                Error(line, "Expected '[['.");
                return false;
            }

            SkipSpaceTab();
            if (!TryParseDottedKey(out name))
                return false;
            SkipSpaceTab();
            if (!Match(']') || !Match(']'))
            {
                Error(line, "Expected ']]' after array table name.");
                return false;
            }

            return true;
        }

        private bool TryParseDottedKey(out string key)
        {
            key = "";
            var parts = new List<string>();
            if (!TryParseKeyPart(out var first))
                return false;
            parts.Add(first);
            while (true)
            {
                SkipSpaceTab();
                if (Peek() != '.')
                    break;
                Advance();
                SkipSpaceTab();
                if (!TryParseKeyPart(out var next))
                    return false;
                parts.Add(next);
            }

            key = string.Join('.', parts);
            return true;
        }

        private bool TryParseKeyPart(out string part)
        {
            part = "";
            if (AtEnd)
            {
                Error(_line, "Expected key.");
                return false;
            }

            var c = Peek();
            if (c is '"' or '\'')
            {
                if (!TryParseString(out var quoted))
                    return false;
                part = quoted;
                return true;
            }

            if (!IsBareKeyChar(c))
            {
                Error(_line, "Expected a key.");
                return false;
            }

            var start = _i;
            while (!AtEnd && IsBareKeyChar(Peek()))
                Advance();
            part = _text[start.._i];
            return true;
        }

        private bool TryParseValue(out TomlValue value)
        {
            value = new TomlStringValue("", _line);
            if (AtEnd)
            {
                Error(_line, "Expected a value.");
                return false;
            }

            var line = _line;
            var c = Peek();
            if (c is '"' or '\'')
            {
                if (!TryParseString(out var s))
                    return false;
                value = new TomlStringValue(s, line);
                return true;
            }

            if (c == '[')
                return TryParseArray(out value);
            if (c == '{')
                return TryParseInlineTable(out value);
            if (c is '+' or '-' || char.IsAsciiDigit(c))
                return TryParseNumber(out value);

            if (TryReadBareWord(out var word))
            {
                if (word.Equals("true", StringComparison.Ordinal))
                {
                    value = new TomlBoolValue(true, line);
                    return true;
                }

                if (word.Equals("false", StringComparison.Ordinal))
                {
                    value = new TomlBoolValue(false, line);
                    return true;
                }

                Error(line, $"Unknown value '{word}'.");
                return false;
            }

            Error(line, "Expected a value.");
            return false;
        }

        private bool TryParseArray(out TomlValue value)
        {
            var line = _line;
            value = new TomlArrayValue([], line);
            if (!Match('['))
            {
                Error(line, "Expected '['.");
                return false;
            }

            var items = new List<TomlValue>();
            while (true)
            {
                SkipWsAndComments();
                if (AtEnd)
                {
                    Error(line, "Unterminated array.");
                    return false;
                }

                if (Peek() == ']')
                {
                    Advance();
                    value = new TomlArrayValue(items, line);
                    return true;
                }

                if (!TryParseValue(out var item))
                    return false;
                items.Add(item);
                SkipWsAndComments();
                if (Peek() == ',')
                {
                    Advance();
                    continue;
                }

                if (Peek() == ']')
                {
                    Advance();
                    value = new TomlArrayValue(items, line);
                    return true;
                }

                Error(_line, "Expected ',' or ']' in array.");
                return false;
            }
        }

        private bool TryParseInlineTable(out TomlValue value)
        {
            var line = _line;
            value = new TomlInlineTableValue([], line);
            if (!Match('{'))
            {
                Error(line, "Expected '{'.");
                return false;
            }

            var fields = new List<TomlAssignment>();
            var fieldPaths = new PathRegistry();
            SkipSpaceTab();
            if (Peek() == '}')
            {
                Advance();
                value = new TomlInlineTableValue(fields, line);
                return true;
            }

            while (true)
            {
                SkipSpaceTab();
                var fieldLine = _line;
                if (!TryParseDottedKey(out var key))
                    return false;
                SkipSpaceTab();
                if (!Match('='))
                {
                    Error(fieldLine, "Expected '=' in inline table.");
                    return false;
                }

                SkipSpaceTab();
                if (!TryParseValue(out var fieldValue))
                    return false;
                if (!fieldPaths.TryAssignValue(key, out var error))
                {
                    Error(fieldLine, error);
                    return false;
                }

                fields.Add(new TomlAssignment(key, fieldValue, fieldLine));
                SkipSpaceTab();
                if (Peek() == ',')
                {
                    Advance();
                    continue;
                }

                if (Peek() == '}')
                {
                    Advance();
                    value = new TomlInlineTableValue(fields, line);
                    return true;
                }

                Error(_line, "Expected ',' or '}' in inline table.");
                return false;
            }
        }

        private bool TryParseNumber(out TomlValue value)
        {
            var line = _line;
            value = new TomlIntValue(0, line);
            var start = _i;
            if (Peek() is '+' or '-')
                Advance();
            if (AtEnd || !char.IsAsciiDigit(Peek()))
            {
                Error(line, "Expected a number.");
                return false;
            }

            while (!AtEnd && (char.IsAsciiDigit(Peek()) || Peek() == '_'))
                Advance();

            var isFloat = false;
            if (!AtEnd && Peek() == '.')
            {
                Advance();
                if (AtEnd || !char.IsAsciiDigit(Peek()))
                {
                    Error(line, "Expected a digit after the decimal point.");
                    return false;
                }

                isFloat = true;
                while (!AtEnd && (char.IsAsciiDigit(Peek()) || Peek() == '_'))
                    Advance();
            }

            if (!AtEnd && Peek() is 'e' or 'E')
            {
                isFloat = true;
                Advance();
                if (!AtEnd && Peek() is '+' or '-')
                    Advance();
                if (AtEnd || !char.IsAsciiDigit(Peek()))
                {
                    Error(line, "Expected a digit in the exponent.");
                    return false;
                }

                while (!AtEnd && (char.IsAsciiDigit(Peek()) || Peek() == '_'))
                    Advance();
            }

            var raw = _text[start.._i].Replace("_", "", StringComparison.Ordinal);
            if (!isFloat)
            {
                if (!long.TryParse(raw, out var n))
                {
                    Error(line, "Integer is out of range.");
                    return false;
                }

                value = new TomlIntValue(n, line);
                return true;
            }

            if (!double.TryParse(
                    raw,
                    System.Globalization.NumberStyles.AllowLeadingSign
                    | System.Globalization.NumberStyles.AllowDecimalPoint
                    | System.Globalization.NumberStyles.AllowExponent,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var real)
                || !double.IsFinite(real))
            {
                Error(line, "Float is out of range.");
                return false;
            }

            value = new TomlFloatValue(real, line);
            return true;
        }

        private bool TryParseString(out string value)
        {
            value = "";
            var line = _line;
            var quote = Peek();
            if (quote is not ('"' or '\''))
            {
                Error(line, "Expected a string.");
                return false;
            }

            Advance();
            if (quote == '"')
                return TryParseBasicString(line, out value);
            return TryParseLiteralString(line, out value);
        }

        private bool TryParseBasicString(int line, out string value)
        {
            var sb = new System.Text.StringBuilder();
            while (!AtEnd)
            {
                var c = Peek();
                if (c == '"')
                {
                    Advance();
                    value = sb.ToString();
                    return true;
                }

                if (c is '\n' or '\r')
                {
                    Error(line, "Unterminated string.");
                    value = "";
                    return false;
                }

                if (c == '\\')
                {
                    Advance();
                    if (AtEnd)
                    {
                        Error(line, "Unterminated string escape.");
                        value = "";
                        return false;
                    }

                    var esc = Advance();
                    sb.Append(esc switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '\\' => '\\',
                        '"' => '"',
                        _ => esc,
                    });
                    continue;
                }

                sb.Append(Advance());
            }

            Error(line, "Unterminated string.");
            value = "";
            return false;
        }

        private bool TryParseLiteralString(int line, out string value)
        {
            var start = _i;
            while (!AtEnd)
            {
                var c = Peek();
                if (c == '\'')
                {
                    value = _text[start.._i];
                    Advance();
                    return true;
                }

                if (c is '\n' or '\r')
                    break;
                Advance();
            }

            Error(line, "Unterminated string.");
            value = "";
            return false;
        }

        private bool TryReadBareWord(out string word)
        {
            word = "";
            if (AtEnd || !char.IsAsciiLetter(Peek()))
                return false;
            var start = _i;
            while (!AtEnd && char.IsAsciiLetter(Peek()))
                Advance();
            word = _text[start.._i];
            return true;
        }

        private void SkipWsAndComments()
        {
            while (!AtEnd)
            {
                var c = Peek();
                if (c is ' ' or '\t')
                {
                    Advance();
                    continue;
                }

                if (c == '\n')
                {
                    Advance();
                    continue;
                }

                if (c == '\r')
                {
                    Advance();
                    if (!AtEnd && Peek() == '\n')
                        Advance();
                    continue;
                }

                if (c == '#')
                {
                    while (!AtEnd && Peek() is not ('\n' or '\r'))
                        Advance();
                    continue;
                }

                break;
            }
        }

        private void SkipSpaceTab()
        {
            while (!AtEnd && Peek() is ' ' or '\t')
                Advance();
        }

        private bool Match(char expected)
        {
            if (AtEnd || Peek() != expected)
                return false;
            Advance();
            return true;
        }

        private bool AtEnd => _i >= _text.Length;

        private char Peek(int ahead = 0)
        {
            var idx = _i + ahead;
            return idx < _text.Length ? _text[idx] : '\0';
        }

        private char Advance()
        {
            var c = _text[_i++];
            if (c == '\n')
                _line++;
            return c;
        }

        private void Error(int line, string message) =>
            _errors.Add(AttachConfigError.Toml(message, line));

        private AttachConfigResult<TomlDocument> Fail() =>
            AttachConfigResult<TomlDocument>.Fail(_errors);

        private static bool IsBareKeyChar(char c) =>
            char.IsAsciiLetterOrDigit(c) || c is '_' or '-';
    }

    /// <summary>
    /// TOML uniqueness: a path is a value or a table once.
    /// Super-tables after <c>[a.b]</c> may open later; dotted-key parents may not.
    /// </summary>
    private sealed class PathRegistry
    {
        private enum Kind
        {
            Value,
            ExplicitTable,
            ImplicitFromHeader,
            ImplicitFromDottedKey,
            ArrayTable,
        }

        private readonly Dictionary<string, Kind> _paths = new(StringComparer.Ordinal);

        public bool TryDefineTable(string name, out string error)
        {
            error = "";
            if (_paths.TryGetValue(name, out var kind))
            {
                if (kind == Kind.ImplicitFromHeader)
                {
                    _paths[name] = Kind.ExplicitTable;
                    return TryMarkParents(name, fromHeader: true, out error);
                }

                error = kind == Kind.Value
                    ? $"Cannot redefine '{name}' as a table."
                    : $"Table '{name}' is already defined.";
                return false;
            }

            if (!TryMarkParents(name, fromHeader: true, out error))
                return false;
            _paths[name] = Kind.ExplicitTable;
            return true;
        }

        public bool TryDefineArrayTable(string name, out string error)
        {
            error = "";
            if (_paths.TryGetValue(name, out var kind) && kind != Kind.ArrayTable)
            {
                error = $"Cannot redefine '{name}' as an array table.";
                return false;
            }

            if (!TryMarkParents(name, fromHeader: true, out error))
                return false;
            _paths[name] = Kind.ArrayTable;
            return true;
        }

        public bool TryAssignValue(string path, out string error)
        {
            error = "";
            if (_paths.TryGetValue(path, out var kind))
            {
                error = kind == Kind.Value
                    ? $"Duplicate key '{path}'."
                    : $"Cannot redefine '{path}' as a value.";
                return false;
            }

            if (!TryMarkParents(path, fromHeader: false, out error))
                return false;
            _paths[path] = Kind.Value;
            return true;
        }

        private bool TryMarkParents(string path, bool fromHeader, out string error)
        {
            error = "";
            var implicitKind = fromHeader ? Kind.ImplicitFromHeader : Kind.ImplicitFromDottedKey;
            var start = 0;
            while (true)
            {
                var dot = path.IndexOf('.', start);
                if (dot < 0)
                    break;

                var parent = path[..dot];
                if (_paths.TryGetValue(parent, out var kind))
                {
                    if (kind == Kind.Value)
                    {
                        error = $"Cannot redefine '{parent}' as a table.";
                        return false;
                    }

                    if (kind == Kind.ArrayTable)
                    {
                        error = $"Cannot define '{path}' under array table '{parent}'.";
                        return false;
                    }
                }
                else
                {
                    _paths[parent] = implicitKind;
                }

                start = dot + 1;
            }

            return true;
        }
    }
}
