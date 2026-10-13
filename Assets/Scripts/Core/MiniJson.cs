using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Shimeji.Core
{
    /// <summary>
    /// 의존성 없는 작은 JSON 파서/작성기.
    /// 객체 = Dictionary&lt;string, object&gt;, 배열 = List&lt;object&gt;, 숫자 = double.
    /// (JsonUtility는 Dictionary를 지원하지 않고 UnityEngine 의존이라 Core에서 쓰지 않는다.)
    /// </summary>
    public static class MiniJson
    {
        public sealed class JsonException : Exception
        {
            public JsonException(string msg) : base(msg) { }
        }

        public static object Parse(string json)
        {
            if (json == null) throw new JsonException("null input");
            var p = new Parser(json);
            p.SkipWs();
            var v = p.ParseValue();
            p.SkipWs();
            if (!p.End) throw new JsonException($"Unexpected trailing data at {p.Pos}");
            return v;
        }

        sealed class Parser
        {
            readonly string _s;
            public int Pos;
            public Parser(string s) { _s = s; }
            public bool End => Pos >= _s.Length;

            public void SkipWs()
            {
                while (Pos < _s.Length)
                {
                    char c = _s[Pos];
                    if (char.IsWhiteSpace(c) || c == '﻿') { Pos++; continue; }
                    // 편의상 // 주석 허용
                    if (c == '/' && Pos + 1 < _s.Length && _s[Pos + 1] == '/')
                    {
                        while (Pos < _s.Length && _s[Pos] != '\n') Pos++;
                        continue;
                    }
                    break;
                }
            }

            public object ParseValue()
            {
                if (End) throw new JsonException("Unexpected end of input");
                char c = _s[Pos];
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                        throw new JsonException($"Unexpected '{c}' at {Pos}");
                }
            }

            void Expect(string word)
            {
                if (string.CompareOrdinal(_s, Pos, word, 0, word.Length) != 0)
                    throw new JsonException($"Expected '{word}' at {Pos}");
                Pos += word.Length;
            }

            Dictionary<string, object> ParseObject()
            {
                var d = new Dictionary<string, object>();
                Pos++; // {
                SkipWs();
                if (!End && _s[Pos] == '}') { Pos++; return d; }
                while (true)
                {
                    SkipWs();
                    if (End || _s[Pos] != '"') throw new JsonException($"Expected key at {Pos}");
                    var key = ParseString();
                    SkipWs();
                    if (End || _s[Pos] != ':') throw new JsonException($"Expected ':' at {Pos}");
                    Pos++;
                    SkipWs();
                    d[key] = ParseValue();
                    SkipWs();
                    if (End) throw new JsonException("Unterminated object");
                    if (_s[Pos] == ',') { Pos++; SkipWs(); if (!End && _s[Pos] == '}') { Pos++; return d; } continue; }
                    if (_s[Pos] == '}') { Pos++; return d; }
                    throw new JsonException($"Expected ',' or '}}' at {Pos}");
                }
            }

            List<object> ParseArray()
            {
                var list = new List<object>();
                Pos++;
                SkipWs();
                if (!End && _s[Pos] == ']') { Pos++; return list; }
                while (true)
                {
                    SkipWs();
                    list.Add(ParseValue());
                    SkipWs();
                    if (End) throw new JsonException("Unterminated array");
                    if (_s[Pos] == ',') { Pos++; SkipWs(); if (!End && _s[Pos] == ']') { Pos++; return list; } continue; }
                    if (_s[Pos] == ']') { Pos++; return list; }
                    throw new JsonException($"Expected ',' or ']' at {Pos}");
                }
            }

            string ParseString()
            {
                var sb = new StringBuilder();
                Pos++; // "
                while (true)
                {
                    if (End) throw new JsonException("Unterminated string");
                    char c = _s[Pos++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }
                    if (End) throw new JsonException("Bad escape");
                    char e = _s[Pos++];
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
                            if (Pos + 4 > _s.Length) throw new JsonException("Bad unicode escape");
                            sb.Append((char)Convert.ToInt32(_s.Substring(Pos, 4), 16));
                            Pos += 4;
                            break;
                        default: throw new JsonException($"Bad escape '\\{e}'");
                    }
                }
            }

            double ParseNumber()
            {
                int start = Pos;
                while (Pos < _s.Length && "+-0123456789.eE".IndexOf(_s[Pos]) >= 0) Pos++;
                var token = _s.Substring(start, Pos - start);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    throw new JsonException($"Bad number '{token}'");
                return d;
            }
        }

        // ---------------- 작성 ----------------

        public static string Serialize(object value, bool pretty = true)
        {
            var sb = new StringBuilder();
            Write(sb, value, pretty, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object v, bool pretty, int indent)
        {
            switch (v)
            {
                case null: sb.Append("null"); return;
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case IDictionary dict:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry e in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, pretty, indent + 1);
                        WriteString(sb, e.Key.ToString());
                        sb.Append(pretty ? ": " : ":");
                        Write(sb, e.Value, pretty, indent + 1);
                    }
                    if (!first) NewLine(sb, pretty, indent);
                    sb.Append('}');
                    return;
                }
                case IEnumerable list:
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (var item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        NewLine(sb, pretty, indent + 1);
                        Write(sb, item, pretty, indent + 1);
                    }
                    if (!first) NewLine(sb, pretty, indent);
                    sb.Append(']');
                    return;
                }
                default:
                    if (v is IConvertible c)
                    {
                        sb.Append(Convert.ToDouble(c, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture));
                        return;
                    }
                    WriteString(sb, v.ToString());
                    return;
            }
        }

        static void NewLine(StringBuilder sb, bool pretty, int indent)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', indent * 2);
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- 읽기 도우미 ----------------

        public static string GetString(this Dictionary<string, object> d, string key, string def = null) =>
            d != null && d.TryGetValue(key, out var v) && v is string s ? s : def;

        public static float GetFloat(this Dictionary<string, object> d, string key, float def) =>
            d != null && d.TryGetValue(key, out var v) && v is double x ? (float)x : def;

        public static int GetInt(this Dictionary<string, object> d, string key, int def) =>
            d != null && d.TryGetValue(key, out var v) && v is double x ? (int)Math.Round(x) : def;

        public static bool GetBool(this Dictionary<string, object> d, string key, bool def) =>
            d != null && d.TryGetValue(key, out var v) && v is bool b ? b : def;

        public static Dictionary<string, object> GetObject(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static List<object> GetArray(this Dictionary<string, object> d, string key) =>
            d != null && d.TryGetValue(key, out var v) ? v as List<object> : null;

        public static string[] GetStringArray(this Dictionary<string, object> d, string key)
        {
            var a = d.GetArray(key);
            if (a == null) return null;
            var r = new List<string>();
            foreach (var o in a) if (o is string s) r.Add(s);
            return r.ToArray();
        }
    }
}
