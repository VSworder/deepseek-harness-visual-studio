using System;
using System.Collections.Generic;
using System.Text;

namespace DeepSeekHarness.Bridge
{
    /// <summary>
    /// Value-level JSON reading for the MCP endpoint.
    /// </summary>
    /// <remarks>
    /// Deliberately not a general parser. It extracts one member at a time by tracking
    /// string state and bracket depth, which is enough for JSON-RPC envelopes and keeps this
    /// assembly free of dependencies 鈥?it has to load inside devenv with nothing else shipped.
    ///
    /// A hand-written reader is only safe if it never guesses: anything the helpers cannot
    /// read confidently comes back as null, and the caller answers with a protocol error.
    /// </remarks>
    internal static class JsonRpc
    {
        /// <summary>Index just past the value of <paramref name="name"/>, or -1.</summary>
        private static int FindValueStart(string json, string name)
        {
            if (string.IsNullOrEmpty(json)) return -1;

            var key = "\"" + name + "\"";
            var from = 0;

            while (true)
            {
                var at = json.IndexOf(key, from, StringComparison.Ordinal);
                if (at < 0) return -1;

                // Only accept the key where a key can legally be: after '{' or ','.
                var probe = at - 1;
                while (probe >= 0 && char.IsWhiteSpace(json[probe])) probe--;
                if (probe < 0 || (json[probe] != '{' && json[probe] != ','))
                {
                    from = at + key.Length;
                    continue;
                }

                var colon = json.IndexOf(':', at + key.Length);
                if (colon < 0) return -1;

                var start = colon + 1;
                while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
                return start < json.Length ? start : -1;
            }
        }

        /// <summary>True when the object carries the named member.</summary>
        public static bool HasMember(string json, string name)
        {
            return FindValueStart(json, name) >= 0;
        }

        /// <summary>The member's raw JSON text, or null. Used for ids, which may be any type.</summary>
        public static string ReadRawMember(string json, string name)
        {
            var start = FindValueStart(json, name);
            if (start < 0) return null;

            var end = ScanValue(json, start);
            return end < 0 ? null : json.Substring(start, end - start);
        }

        /// <summary>The member as a string, or null when absent or not a string.</summary>
        public static string ReadString(string json, string name)
        {
            var start = FindValueStart(json, name);
            if (start < 0 || json[start] != '"') return null;

            return ReadQuoted(json, start, out _);
        }

        /// <summary>
        /// Reads a <c>tools/call</c> arguments object into a string map. Nested objects and
        /// arrays are preserved as raw JSON text, which is what the tools see.
        /// </summary>
        public static IReadOnlyDictionary<string, string> ReadArguments(string argumentsJson)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(argumentsJson)) return result;

            var text = argumentsJson.Trim();
            if (text.Length < 2 || text[0] != '{') return result;

            var i = 1;
            while (i < text.Length)
            {
                while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
                if (i >= text.Length || text[i] == '}') break;
                if (text[i] != '"') break;

                var key = ReadQuoted(text, i, out var afterKey);
                if (key == null) break;

                i = afterKey;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length || text[i] != ':') break;
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;

                var end = ScanValue(text, i);
                if (end < 0) break;

                var raw = text.Substring(i, end - i);

                // Unwrap plain strings so tools receive the value, not its JSON encoding.
                result[key] = raw.Length > 0 && raw[0] == '"' ? ReadQuoted(raw, 0, out _) : raw;
                i = end;
            }

            return result;
        }

        /// <summary>Returns the index just past the value starting at <paramref name="start"/>.</summary>
        private static int ScanValue(string json, int start)
        {
            if (start < 0 || start >= json.Length) return -1;

            var c = json[start];

            if (c == '"')
            {
                var skipped = ReadQuoted(json, start, out var after);
                return skipped == null ? -1 : after;
            }

            if (c == '{' || c == '[')
            {
                var depth = 0;
                var inString = false;

                for (var i = start; i < json.Length; i++)
                {
                    var ch = json[i];

                    if (inString)
                    {
                        if (ch == '\\') i++;
                        else if (ch == '"') inString = false;
                        continue;
                    }

                    if (ch == '"') inString = true;
                    else if (ch == '{' || ch == '[') depth++;
                    else if (ch == '}' || ch == ']')
                    {
                        depth--;
                        if (depth == 0) return i + 1;
                    }
                }

                return -1;
            }

            // Literal: number, true, false, null.
            var end = start;
            while (end < json.Length && json[end] != ',' && json[end] != '}' && json[end] != ']' &&
                   !char.IsWhiteSpace(json[end]))
                end++;

            return end;
        }

        /// <summary>Reads a JSON string literal starting at the opening quote.</summary>
        private static string ReadQuoted(string json, int start, out int afterEnd)
        {
            afterEnd = -1;
            if (start >= json.Length || json[start] != '"') return null;

            var sb = new StringBuilder();

            for (var i = start + 1; i < json.Length; i++)
            {
                var c = json[i];

                if (c == '\\')
                {
                    if (++i >= json.Length) return null;
                    var esc = json[i];
                    switch (esc)
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
                            if (i + 4 >= json.Length) return null;
                            sb.Append((char)Convert.ToInt32(json.Substring(i + 1, 4), 16));
                            i += 4;
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else if (c == '"')
                {
                    afterEnd = i + 1;
                    return sb.ToString();
                }
                else
                {
                    sb.Append(c);
                }
            }

            return null;
        }

        /// <summary>Encodes a string as a JSON literal, including the surrounding quotes.</summary>
        public static string Quote(string value)
        {
            if (value == null) return "null";

            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');

            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
