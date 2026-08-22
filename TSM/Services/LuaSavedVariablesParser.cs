using System.Globalization;
using System.Text;
using TSM.Models;

namespace TSM.Services;

public sealed class LuaSavedVariablesParser
{
    private const int MaximumDepth = 256;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedDocument> _fileCache = new(StringComparer.OrdinalIgnoreCase);
    private Lexer _lexer = null!;
    private Token _current;

    public LuaDocument Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
            return ParseCore(source);
    }

    public LuaDocument ParseFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new FileNotFoundException("SavedVariables file was not found.", fullPath);

        lock (_gate)
        {
            if (_fileCache.TryGetValue(fullPath, out var cached) &&
                cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks && cached.Length == info.Length)
                return cached.Document;

            var document = ParseCore(File.ReadAllText(fullPath, Encoding.UTF8));
            _fileCache[fullPath] = new CachedDocument(info.LastWriteTimeUtc.Ticks, info.Length, document);
            return document;
        }
    }

    private LuaDocument ParseCore(string source)
    {
        _lexer = new Lexer(source);
        _current = _lexer.Next();
        var globals = new Dictionary<string, object?>(StringComparer.Ordinal);

        while (_current.Kind != TokenKind.End)
        {
            var name = Expect(TokenKind.Identifier, "Expected a global variable name").Text;
            Expect(TokenKind.Equals, "Expected '=' after the variable name");
            globals[name] = ParseValue(0);
            ConsumeSeparators();
        }

        return new LuaDocument(globals);
    }

    private object? ParseValue(int depth)
    {
        if (depth > MaximumDepth)
            throw Error("Maximum table nesting depth exceeded");

        return _current.Kind switch
        {
            TokenKind.String => Advance().Text,
            TokenKind.Number => ParseNumber(Advance()),
            TokenKind.True => AdvanceAndReturn(true),
            TokenKind.False => AdvanceAndReturn(false),
            TokenKind.Nil => AdvanceAndReturn<object?>(null),
            TokenKind.LeftBrace => ParseTable(depth + 1),
            _ => throw Error("Expected a SavedVariables value")
        };
    }

    private LuaTable ParseTable(int depth)
    {
        Expect(TokenKind.LeftBrace, "Expected '{'");
        var table = new LuaTable();
        long nextArrayIndex = 1;

        while (_current.Kind != TokenKind.RightBrace)
        {
            if (_current.Kind == TokenKind.End)
                throw Error("Unterminated table");

            object key;
            object? value;
            if (_current.Kind == TokenKind.LeftBracket)
            {
                Advance();
                key = ParseKey();
                Expect(TokenKind.RightBracket, "Expected ']' after table key");
                Expect(TokenKind.Equals, "Expected '=' after table key");
                value = ParseValue(depth);
            }
            else if (_current.Kind == TokenKind.Identifier && _lexer.Peek().Kind == TokenKind.Equals)
            {
                key = Advance().Text;
                Expect(TokenKind.Equals, "Expected '=' after table key");
                value = ParseValue(depth);
            }
            else
            {
                key = nextArrayIndex++;
                value = ParseValue(depth);
            }

            if (value is not null)
                table.Add(key, value);

            if (_current.Kind is TokenKind.Comma or TokenKind.Semicolon)
                ConsumeSeparators();
            else if (_current.Kind != TokenKind.RightBrace)
                throw Error("Expected ',' or '}' after table value");
        }

        Advance();
        return table;
    }

    private object ParseKey()
    {
        return _current.Kind switch
        {
            TokenKind.String => Advance().Text,
            TokenKind.Number => ParseNumber(Advance()),
            TokenKind.True => AdvanceAndReturn(true),
            TokenKind.False => AdvanceAndReturn(false),
            _ => throw Error("Table keys must be strings, numbers, or booleans")
        };
    }

    private static object ParseNumber(Token token)
    {
        if (token.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(token.Text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexadecimal))
            return hexadecimal;

        if (long.TryParse(token.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
            return integer;

        if (double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            return real;

        throw new LuaParseException($"Invalid number '{token.Text}'", token.Line, token.Column);
    }

    private T AdvanceAndReturn<T>(T value)
    {
        Advance();
        return value;
    }

    private Token Advance()
    {
        var previous = _current;
        _current = _lexer.Next();
        return previous;
    }

    private Token Expect(TokenKind kind, string message)
    {
        if (_current.Kind != kind)
            throw Error(message);
        return Advance();
    }

    private void ConsumeSeparators()
    {
        while (_current.Kind is TokenKind.Comma or TokenKind.Semicolon)
            Advance();
    }

    private LuaParseException Error(string message) => new(message, _current.Line, _current.Column);

    private enum TokenKind
    {
        End,
        Identifier,
        String,
        Number,
        True,
        False,
        Nil,
        LeftBrace,
        RightBrace,
        LeftBracket,
        RightBracket,
        Equals,
        Comma,
        Semicolon
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Line, int Column);
    private sealed record CachedDocument(long LastWriteTicks, long Length, LuaDocument Document);

    private sealed class Lexer(string source)
    {
        private int _offset;
        private int _line = 1;
        private int _column = 1;
        private Token? _peeked;

        public Token Peek() => _peeked ??= ReadNext();

        public Token Next()
        {
            if (_peeked is { } token)
            {
                _peeked = null;
                return token;
            }

            return ReadNext();
        }

        private Token ReadNext()
        {
            SkipTrivia();
            if (AtEnd)
                return new Token(TokenKind.End, string.Empty, _line, _column);

            var line = _line;
            var column = _column;
            var character = Current;
            switch (character)
            {
                case '{': Advance(); return new Token(TokenKind.LeftBrace, "{", line, column);
                case '}': Advance(); return new Token(TokenKind.RightBrace, "}", line, column);
                case '[': Advance(); return new Token(TokenKind.LeftBracket, "[", line, column);
                case ']': Advance(); return new Token(TokenKind.RightBracket, "]", line, column);
                case '=': Advance(); return new Token(TokenKind.Equals, "=", line, column);
                case ',': Advance(); return new Token(TokenKind.Comma, ",", line, column);
                case ';': Advance(); return new Token(TokenKind.Semicolon, ";", line, column);
                case '\'':
                case '"':
                    return ReadString(character, line, column);
            }

            if (char.IsAsciiLetter(character) || character == '_')
                return ReadIdentifier(line, column);
            if (char.IsAsciiDigit(character) || character is '-' or '+')
                return ReadNumber(line, column);

            throw new LuaParseException($"Unexpected character '{character}'", line, column);
        }

        private Token ReadIdentifier(int line, int column)
        {
            var start = _offset;
            while (!AtEnd && (char.IsAsciiLetterOrDigit(Current) || Current == '_'))
                Advance();
            var text = source[start.._offset];
            var kind = text switch
            {
                "true" => TokenKind.True,
                "false" => TokenKind.False,
                "nil" => TokenKind.Nil,
                _ => TokenKind.Identifier
            };
            return new Token(kind, text, line, column);
        }

        private Token ReadNumber(int line, int column)
        {
            var start = _offset;
            if (Current is '-' or '+')
                Advance();

            if (!AtEnd && Current == '0' && PeekCharacter(1) is 'x' or 'X')
            {
                Advance();
                Advance();
                while (!AtEnd && Uri.IsHexDigit(Current))
                    Advance();
            }
            else
            {
                while (!AtEnd && (char.IsAsciiDigit(Current) || Current is '.' or 'e' or 'E' or '+' or '-'))
                    Advance();
            }

            return new Token(TokenKind.Number, source[start.._offset], line, column);
        }

        private Token ReadString(char quote, int line, int column)
        {
            Advance();
            var value = new StringBuilder();
            while (!AtEnd)
            {
                var character = Current;
                Advance();
                if (character == quote)
                    return new Token(TokenKind.String, value.ToString(), line, column);

                if (character != '\\')
                {
                    value.Append(character);
                    continue;
                }

                if (AtEnd)
                    throw new LuaParseException("Unterminated escape sequence", _line, _column);

                var escaped = Current;
                Advance();
                switch (escaped)
                {
                    case 'a': value.Append('\a'); break;
                    case 'b': value.Append('\b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'v': value.Append('\v'); break;
                    case '\\': value.Append('\\'); break;
                    case '"': value.Append('"'); break;
                    case '\'': value.Append('\''); break;
                    case '\n': break;
                    case 'x': value.Append((char)ReadDigits(2, 16)); break;
                    case 'z':
                        while (!AtEnd && char.IsWhiteSpace(Current))
                            Advance();
                        break;
                    default:
                        if (char.IsAsciiDigit(escaped))
                        {
                            var digits = escaped.ToString();
                            for (var count = 1; count < 3 && !AtEnd && char.IsAsciiDigit(Current); count++)
                            {
                                digits += Current;
                                Advance();
                            }
                            value.Append((char)int.Parse(digits, CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            value.Append(escaped);
                        }
                        break;
                }
            }

            throw new LuaParseException("Unterminated string", line, column);
        }

        private int ReadDigits(int count, int numberBase)
        {
            if (_offset + count > source.Length)
                throw new LuaParseException("Incomplete numeric escape", _line, _column);
            var text = source.Substring(_offset, count);
            if (text.Any(character => !Uri.IsHexDigit(character)))
                throw new LuaParseException("Invalid numeric escape", _line, _column);
            for (var index = 0; index < count; index++)
                Advance();
            return Convert.ToInt32(text, numberBase);
        }

        private void SkipTrivia()
        {
            while (true)
            {
                while (!AtEnd && char.IsWhiteSpace(Current))
                    Advance();

                if (AtEnd || Current != '-' || PeekCharacter(1) != '-')
                    return;

                Advance();
                Advance();
                if (!AtEnd && Current == '[' && PeekCharacter(1) == '[')
                {
                    Advance();
                    Advance();
                    while (!AtEnd && !(Current == ']' && PeekCharacter(1) == ']'))
                        Advance();
                    if (!AtEnd)
                    {
                        Advance();
                        Advance();
                    }
                }
                else
                {
                    while (!AtEnd && Current != '\n')
                        Advance();
                }
            }
        }

        private bool AtEnd => _offset >= source.Length;
        private char Current => source[_offset];
        private char PeekCharacter(int distance) => _offset + distance < source.Length ? source[_offset + distance] : '\0';

        private void Advance()
        {
            if (AtEnd)
                return;
            if (source[_offset++] == '\n')
            {
                _line++;
                _column = 1;
            }
            else
            {
                _column++;
            }
        }
    }
}
