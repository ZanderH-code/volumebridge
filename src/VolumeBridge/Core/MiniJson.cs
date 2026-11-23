using System.Globalization;
using System.Text;

namespace VolumeBridge.Core;

/// <summary>
/// Tiny self-contained JSON parser/writer (supports // comments and trailing commas for config.json).
/// Keeps net48 builds free of System.Text.Json's runtime DLL dependencies.
/// </summary>
public static class MiniJson
{
    public static object? Parse(string json)
    {
        var p = new Parser(json);
        object? value = p.ReadValue();
        p.SkipTrivia();
        if (!p.End) throw new FormatException($"Unexpected trailing character '{p.Current}' at position {p.Pos}");
        return value;
    }

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    sealed class Parser(string s)
    {
        int _i;

        public int Pos => _i;
        public bool End => _i >= s.Length;
        public char Current => s[_i];

        public void SkipTrivia()
        {
            while (_i < s.Length)
            {
                char c = s[_i];
                if (c is ' ' or '\t' or '\r' or '\n') { _i++; continue; }
                if (c == '/' && _i + 1 < s.Length && s[_i + 1] == '/')
                {
                    _i += 2;
                    while (_i < s.Length && s[_i] != '\n') _i++;
                    continue;
                }
                if (c == '/' && _i + 1 < s.Length && s[_i + 1] == '*')
                {
                    _i += 2;
                    int end = s.IndexOf("*/", _i, StringComparison.Ordinal);
                    if (end < 0) throw new FormatException("Unterminated block comment");
                    _i = end + 2;
                    continue;
                }
                break;
            }
        }

        public object? ReadValue()
        {
            SkipTrivia();
            if (End) throw new FormatException("Unexpected end of JSON");
            return s[_i] switch
            {
                '{' => ReadObject(),
                '[' => ReadArray(),
                '"' => ReadString(),
                't' => ReadLiteral("true", true),
                'f' => ReadLiteral("false", false),
                'n' => ReadLiteral("null", null),
                '-' or (>= '0' and <= '9') => ReadNumber(),
                _ => throw new FormatException($"Unexpected character '{s[_i]}' at position {_i}"),
            };
        }

        Dictionary<string, object?> ReadObject()
        {
            _i++; // '{'
            var obj = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipTrivia();
                if (End) throw new FormatException("Unterminated JSON object");
                if (s[_i] == '}') { _i++; return obj; }
                if (s[_i] != '"') throw new FormatException($"Expected string key at position {_i}");
                string key = ReadString();
                SkipTrivia();
                if (End || s[_i] != ':') throw new FormatException($"Expected ':' at position {_i}");
                _i++;
                obj[key] = ReadValue();
                SkipTrivia();
                if (End) throw new FormatException("Unterminated JSON object");
                if (s[_i] == ',') { _i++; continue; }
                if (s[_i] == '}') { _i++; return obj; }
                throw new FormatException($"Expected ',' or '}}' at position {_i}");
            }
        }

        List<object?> ReadArray()
        {
            _i++; // '['
            var list = new List<object?>();
            while (true)
            {
                SkipTrivia();
                if (End) throw new FormatException("Unterminated JSON array");
                if (s[_i] == ']') { _i++; return list; }
                list.Add(ReadValue());
                SkipTrivia();
                if (End) throw new FormatException("Unterminated JSON array");
                if (s[_i] == ',') { _i++; continue; }
                if (s[_i] == ']') { _i++; return list; }
                throw new FormatException($"Expected ',' or ']' at position {_i}");
            }
        }

        string ReadString()
        {
            _i++; // '"'
            var sb = new StringBuilder();
            while (_i < s.Length)
            {
                char c = s[_i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (_i >= s.Length) throw new FormatException("Unterminated string escape");
                    char e = s[_i++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_i + 4 > s.Length) throw new FormatException("Incomplete \\u escape");
                            string hex = s.Substring(_i, 4);
                            if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                                throw new FormatException($"Invalid \\u escape: \\u{hex}");
                            sb.Append((char)code);
                            _i += 4;
                            break;
                        default:
                            throw new FormatException($"Invalid escape '\\{e}'");
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            throw new FormatException("Unterminated JSON string");
        }

        double ReadNumber()
        {
            int start = _i;
            if (s[_i] == '-') _i++;
            while (_i < s.Length && s[_i] is >= '0' and <= '9') _i++;
            if (_i < s.Length && s[_i] == '.')
            {
                _i++;
                while (_i < s.Length && s[_i] is >= '0' and <= '9') _i++;
            }
            if (_i < s.Length && s[_i] is 'e' or 'E')
            {
                _i++;
                if (_i < s.Length && s[_i] is '+' or '-') _i++;
                while (_i < s.Length && s[_i] is >= '0' and <= '9') _i++;
            }
            string num = s.Substring(start, _i - start);
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                throw new FormatException($"Invalid JSON number '{num}'");
            return value;
        }

        object? ReadLiteral(string expected, object? result)
        {
            if (_i + expected.Length > s.Length || string.CompareOrdinal(s, _i, expected, 0, expected.Length) != 0)
                throw new FormatException($"Expected '{expected}' at position {_i}");
            _i += expected.Length;
            return result;
        }
    }
}
