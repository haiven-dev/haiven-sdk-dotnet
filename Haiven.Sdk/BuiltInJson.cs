// The SDK's own JSON reader and writer: the default codec.
//
// Written here rather than pulled in, for the reason the C++ SDK carries
// HaivenJson.h: the JSON library every .NET target has in the box is not in the
// box for Unity, and the one Unity does ship (JsonUtility) cannot say whether a
// field was absent. It reads untrusted server input, so it bounds nesting depth,
// rejects anything RFC 8259 does not allow, and converts numbers with the
// invariant culture whatever culture a game has set. It is only as much JSON as
// the wire uses: no comments, no trailing commas, no streaming.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Haiven.Sdk.Json
{
    /// <summary>The default codec: no dependencies, no reflection, AOT- and IL2CPP-safe.</summary>
    public sealed class BuiltInJsonCodec : IHaivenJsonCodec
    {
        /// <summary>The nesting depth a document may reach; System.Text.Json's default.</summary>
        public const int MaxDepth = 64;

        public static readonly BuiltInJsonCodec Instance = new BuiltInJsonCodec();

        private BuiltInJsonCodec()
        {
        }

        public string Name => "builtin";

        public IHaivenJsonNode Parse(string json)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));
            return new JsonParser(json).ParseDocument();
        }

        public string Write(Action<IHaivenJsonWriter> write)
        {
            if (write is null) throw new ArgumentNullException(nameof(write));
            var writer = new HaivenJsonTextWriter();
            write(writer);
            return writer.ToString();
        }

        public bool TryWriteUnknown(IHaivenJsonWriter writer, object value) => false;

        public bool TryReadUnknown(IHaivenJsonNode node, Type type, out object? value)
        {
            value = null;
            return false;
        }
    }

    /// <summary>A parsed value from <see cref="BuiltInJsonCodec"/>.</summary>
    public sealed class HaivenJsonValue : IHaivenJsonNode
    {
        public static readonly HaivenJsonValue Null = new HaivenJsonValue(HaivenJsonKind.Null, null, null, null);
        public static readonly HaivenJsonValue True = new HaivenJsonValue(HaivenJsonKind.True, null, null, null);
        public static readonly HaivenJsonValue False = new HaivenJsonValue(HaivenJsonKind.False, null, null, null);

        private static readonly KeyValuePair<string, IHaivenJsonNode>[] NoMembers = new KeyValuePair<string, IHaivenJsonNode>[0];
        private static readonly IHaivenJsonNode[] NoItems = new IHaivenJsonNode[0];

        // The scalar's text: a string's decoded value, or a number's source text.
        private readonly string? text;
        private readonly List<KeyValuePair<string, IHaivenJsonNode>>? members;
        private readonly List<IHaivenJsonNode>? items;

        private HaivenJsonValue(
            HaivenJsonKind kind,
            string? text,
            List<KeyValuePair<string, IHaivenJsonNode>>? members,
            List<IHaivenJsonNode>? items)
        {
            Kind = kind;
            this.text = text;
            this.members = members;
            this.items = items;
        }

        public HaivenJsonKind Kind { get; }

        public static HaivenJsonValue String(string value) =>
            new HaivenJsonValue(HaivenJsonKind.String, value ?? throw new ArgumentNullException(nameof(value)), null, null);

        public static HaivenJsonValue Boolean(bool value) => value ? True : False;

        internal static HaivenJsonValue Number(string literal) =>
            new HaivenJsonValue(HaivenJsonKind.Number, literal, null, null);

        internal static HaivenJsonValue Object(List<KeyValuePair<string, IHaivenJsonNode>> members) =>
            new HaivenJsonValue(HaivenJsonKind.Object, null, members, null);

        internal static HaivenJsonValue Array(List<IHaivenJsonNode> items) =>
            new HaivenJsonValue(HaivenJsonKind.Array, null, null, items);

        /// <summary>
        /// The last member with this name, as System.Text.Json's deserializer
        /// resolves a duplicate.
        /// </summary>
        public bool TryGetProperty(string name, out IHaivenJsonNode value)
        {
            if (members != null)
            {
                for (var index = members.Count - 1; index >= 0; index--)
                {
                    if (string.Equals(members[index].Key, name, StringComparison.Ordinal))
                    {
                        value = members[index].Value;
                        return true;
                    }
                }
            }

            value = Null;
            return false;
        }

        public IEnumerable<KeyValuePair<string, IHaivenJsonNode>> EnumerateObject() =>
            (IEnumerable<KeyValuePair<string, IHaivenJsonNode>>?)members ?? NoMembers;

        public IEnumerable<IHaivenJsonNode> EnumerateArray() => (IEnumerable<IHaivenJsonNode>?)items ?? NoItems;

        public string GetString() =>
            Kind == HaivenJsonKind.String ? text! : throw new HaivenJsonException($"haiven: expected a string, got {Kind}");

        public string GetNumberText() =>
            Kind == HaivenJsonKind.Number ? text! : throw new HaivenJsonException($"haiven: expected a number, got {Kind}");

        public string ToJson()
        {
            var writer = new HaivenJsonTextWriter();
            writer.WriteNode(this);
            return writer.ToString();
        }

        public override string ToString() => ToJson();
    }

    /// <summary>The built-in writer: compact JSON into a string.</summary>
    public sealed class HaivenJsonTextWriter : IHaivenJsonWriter
    {
        private readonly StringBuilder output = new StringBuilder();

        // One entry per open container: whether it has a value yet, so the next one needs a comma.
        private readonly Stack<bool> open = new Stack<bool>();
        private bool afterName;

        public void WriteStartObject()
        {
            CheckDepth();
            BeforeValue();
            output.Append('{');
            open.Push(false);
        }

        public void WriteEndObject()
        {
            Close('}');
        }

        public void WriteStartArray()
        {
            CheckDepth();
            BeforeValue();
            output.Append('[');
            open.Push(false);
        }

        public void WriteEndArray()
        {
            Close(']');
        }

        public void WritePropertyName(string name)
        {
            if (open.Count == 0 || afterName) throw new InvalidOperationException("haiven: a property name outside an object");
            Separate();
            AppendString(name);
            output.Append(':');
            afterName = true;
        }

        public void WriteString(string value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            BeforeValue();
            AppendString(value);
        }

        public void WriteNumber(long value)
        {
            BeforeValue();
            output.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public void WriteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "haiven: JSON has no NaN or infinity");
            }

            BeforeValue();
            output.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        public void WriteBoolean(bool value)
        {
            BeforeValue();
            output.Append(value ? "true" : "false");
        }

        public void WriteNull()
        {
            BeforeValue();
            output.Append("null");
        }

        public void WriteNode(IHaivenJsonNode value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            switch (value.Kind)
            {
                case HaivenJsonKind.Null: WriteNull(); break;
                case HaivenJsonKind.True: WriteBoolean(true); break;
                case HaivenJsonKind.False: WriteBoolean(false); break;
                case HaivenJsonKind.String: WriteString(value.GetString()); break;
                case HaivenJsonKind.Number:
                    // The source text, so a 64-bit id or an exact decimal survives the copy.
                    BeforeValue();
                    output.Append(value.GetNumberText());
                    break;
                case HaivenJsonKind.Array:
                    WriteStartArray();
                    foreach (var item in value.EnumerateArray()) WriteNode(item);
                    WriteEndArray();
                    break;
                default:
                    WriteStartObject();
                    foreach (var member in value.EnumerateObject())
                    {
                        WritePropertyName(member.Key);
                        WriteNode(member.Value);
                    }

                    WriteEndObject();
                    break;
            }
        }

        public override string ToString()
        {
            if (open.Count != 0) throw new InvalidOperationException("haiven: the JSON document is not closed");
            return output.ToString();
        }

        private void BeforeValue()
        {
            if (afterName)
            {
                afterName = false;
                return;
            }

            if (open.Count == 0)
            {
                if (output.Length != 0) throw new InvalidOperationException("haiven: a second top-level JSON value");
                return;
            }

            Separate();
        }

        private void CheckDepth()
        {
            if (open.Count >= BuiltInJsonCodec.MaxDepth)
            {
                throw new HaivenJsonException(
                    $"haiven: JSON exceeds the maximum nesting depth of {BuiltInJsonCodec.MaxDepth}");
            }
        }

        private void Separate()
        {
            if (open.Pop()) output.Append(',');
            open.Push(true);
        }

        private void Close(char bracket)
        {
            if (open.Count == 0 || afterName) throw new InvalidOperationException("haiven: unbalanced JSON writer");
            open.Pop();
            output.Append(bracket);
        }

        private void AppendString(string value)
        {
            output.Append('"');
            for (var index = 0; index < value.Length; index++)
            {
                var c = value[index];
                switch (c)
                {
                    case '"': output.Append("\\\""); continue;
                    case '\\': output.Append("\\\\"); continue;
                    case '\n': output.Append("\\n"); continue;
                    case '\r': output.Append("\\r"); continue;
                    case '\t': output.Append("\\t"); continue;
                    case '\b': output.Append("\\b"); continue;
                    case '\f': output.Append("\\f"); continue;
                }

                if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029)
                {
                    AppendEscape(c);
                }
                else if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    output.Append(c).Append(value[++index]);
                }
                else if (char.IsSurrogate(c))
                {
                    // A lone surrogate is not text; escaped, it is at least still JSON.
                    AppendEscape(c);
                }
                else
                {
                    output.Append(c);
                }
            }

            output.Append('"');
        }

        private void AppendEscape(char c)
        {
            output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
    }

    internal sealed class JsonParser
    {
        private readonly string text;
        private int position;
        private int depth;

        public JsonParser(string text)
        {
            this.text = text;
        }

        public HaivenJsonValue ParseDocument()
        {
            SkipWhitespace();
            var value = ParseValue();
            SkipWhitespace();
            if (position != text.Length) throw Error("unexpected text after the JSON value");
            return value;
        }

        private HaivenJsonValue ParseValue()
        {
            if (position >= text.Length) throw Error("unexpected end of JSON");
            switch (text[position])
            {
                case '{': return ParseObject();
                case '[': return ParseArray();
                case '"': return HaivenJsonValue.String(ParseString());
                case 't': Literal("true"); return HaivenJsonValue.True;
                case 'f': Literal("false"); return HaivenJsonValue.False;
                case 'n': Literal("null"); return HaivenJsonValue.Null;
                default: return ParseNumber();
            }
        }

        private HaivenJsonValue ParseObject()
        {
            Enter();
            position++;
            var members = new List<KeyValuePair<string, IHaivenJsonNode>>();
            SkipWhitespace();
            if (Peek() == '}')
            {
                position++;
                depth--;
                return HaivenJsonValue.Object(members);
            }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw Error("expected a member name");
                var name = ParseString();
                SkipWhitespace();
                Expect(':');
                SkipWhitespace();
                members.Add(new KeyValuePair<string, IHaivenJsonNode>(name, ParseValue()));
                SkipWhitespace();
                var next = Next();
                if (next == '}') break;
                if (next != ',') throw Error("expected ',' or '}'", position - 1);
            }

            depth--;
            return HaivenJsonValue.Object(members);
        }

        private HaivenJsonValue ParseArray()
        {
            Enter();
            position++;
            var items = new List<IHaivenJsonNode>();
            SkipWhitespace();
            if (Peek() == ']')
            {
                position++;
                depth--;
                return HaivenJsonValue.Array(items);
            }

            while (true)
            {
                SkipWhitespace();
                items.Add(ParseValue());
                SkipWhitespace();
                var next = Next();
                if (next == ']') break;
                if (next != ',') throw Error("expected ',' or ']'", position - 1);
            }

            depth--;
            return HaivenJsonValue.Array(items);
        }

        private string ParseString()
        {
            position++;
            StringBuilder? built = null;
            var start = position;
            while (true)
            {
                if (position >= text.Length) throw Error("unterminated string");
                var c = text[position];
                if (c == '"')
                {
                    var tail = text.Substring(start, position - start);
                    position++;
                    return built is null ? tail : built.Append(tail).ToString();
                }

                if (c < 0x20) throw Error("a control character in a string");
                if (c != '\\')
                {
                    position++;
                    continue;
                }

                built ??= new StringBuilder();
                built.Append(text, start, position - start);
                position++;
                if (position >= text.Length) throw Error("unterminated escape");
                var escape = text[position++];
                switch (escape)
                {
                    case '"': built.Append('"'); break;
                    case '\\': built.Append('\\'); break;
                    case '/': built.Append('/'); break;
                    case 'b': built.Append('\b'); break;
                    case 'f': built.Append('\f'); break;
                    case 'n': built.Append('\n'); break;
                    case 'r': built.Append('\r'); break;
                    case 't': built.Append('\t'); break;
                    case 'u': built.Append(ParseHex()); break;
                    default: throw Error("an invalid escape", position - 1);
                }

                start = position;
            }
        }

        private char ParseHex()
        {
            if (position + 4 > text.Length) throw Error("a truncated \\u escape");
            var value = 0;
            for (var index = 0; index < 4; index++)
            {
                var c = text[position++];
                int digit;
                if (c >= '0' && c <= '9') digit = c - '0';
                else if (c >= 'a' && c <= 'f') digit = c - 'a' + 10;
                else if (c >= 'A' && c <= 'F') digit = c - 'A' + 10;
                else throw Error("an invalid \\u escape", position - 1);
                value = (value << 4) | digit;
            }

            return (char)value;
        }

        private HaivenJsonValue ParseNumber()
        {
            var start = position;
            if (Peek() == '-') position++;
            if (Peek() == '0')
            {
                position++;
            }
            else if (IsDigit(Peek()))
            {
                while (IsDigit(Peek())) position++;
            }
            else
            {
                throw Error("an unexpected character", start);
            }

            if (Peek() == '.')
            {
                position++;
                if (!IsDigit(Peek())) throw Error("a digit after the decimal point");
                while (IsDigit(Peek())) position++;
            }

            if (Peek() == 'e' || Peek() == 'E')
            {
                position++;
                if (Peek() == '+' || Peek() == '-') position++;
                if (!IsDigit(Peek())) throw Error("a digit in the exponent");
                while (IsDigit(Peek())) position++;
            }

            return HaivenJsonValue.Number(text.Substring(start, position - start));
        }

        private void Literal(string literal)
        {
            if (string.CompareOrdinal(text, position, literal, 0, literal.Length) != 0) throw Error("an unexpected character");
            position += literal.Length;
        }

        private void Enter()
        {
            if (++depth > BuiltInJsonCodec.MaxDepth) throw Error($"nesting deeper than {BuiltInJsonCodec.MaxDepth}");
        }

        private void Expect(char expected)
        {
            if (Next() != expected) throw Error($"expected '{expected}'", position - 1);
        }

        private char Next() => position < text.Length ? text[position++] : throw Error("unexpected end of JSON");

        private char Peek() => position < text.Length ? text[position] : '\0';

        private void SkipWhitespace()
        {
            while (position < text.Length)
            {
                var c = text[position];
                if (c != ' ' && c != '\t' && c != '\n' && c != '\r') return;
                position++;
            }
        }

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private HaivenJsonException Error(string message, int? at = null) =>
            new HaivenJsonException($"haiven: malformed JSON at offset {at ?? position}: {message}");
    }
}
