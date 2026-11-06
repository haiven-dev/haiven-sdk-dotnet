// The JSON seam: what the SDK needs from a JSON library, and nothing more.
//
// The SDK used to call System.Text.Json directly, reflectively, from five places
// (the session's frame writer, the WebSocket payload decoder, and the REST
// client's body reader, writer and form encoder). That made every Unity and
// IL2CPP consumer inherit a package that is not in netstandard2.1, and a
// reflective serializer whose targets the linker cannot see.
//
// Now the generated code reads and writes through two small interfaces: a parsed
// node it can ask "is this member present?" (IHaivenJsonNode), and a forward-only
// writer (IHaivenJsonWriter). Every wire type gets a generated read and write
// method over them (ContractsJson.g.cs, RestApi.g.cs), so nothing is constructed
// or enumerated by reflection. Which library sits behind the interfaces is a
// choice (IHaivenJsonCodec): the SDK's own reader and writer by default
// (BuiltInJsonCodec), or System.Text.Json through the opt-in
// Haiven.Sdk.SystemTextJson adapter.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Haiven.Sdk.Json
{
    /// <summary>The kind of a parsed JSON value.</summary>
    public enum HaivenJsonKind
    {
        Null,
        False,
        True,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>
    /// A parsed JSON value, read-only. Presence-aware by construction: a member
    /// that was not on the wire is not a property of its object, which is
    /// different from a member that was there as <c>null</c>.
    /// </summary>
    public interface IHaivenJsonNode
    {
        HaivenJsonKind Kind { get; }

        /// <summary>The member called <paramref name="name"/>, when this is an object that has one.</summary>
        bool TryGetProperty(string name, out IHaivenJsonNode value);

        /// <summary>An object's members in document order; empty for anything else.</summary>
        IEnumerable<KeyValuePair<string, IHaivenJsonNode>> EnumerateObject();

        /// <summary>An array's items in order; empty for anything else.</summary>
        IEnumerable<IHaivenJsonNode> EnumerateArray();

        /// <summary>A string's value; throws <see cref="HaivenJsonException"/> for anything else.</summary>
        string GetString();

        /// <summary>A number's exact source text; throws for anything else.</summary>
        string GetNumberText();

        /// <summary>The value as compact JSON text.</summary>
        string ToJson();
    }

    /// <summary>A forward-only JSON writer. Generated code writes every wire type through one.</summary>
    public interface IHaivenJsonWriter
    {
        void WriteStartObject();
        void WriteEndObject();
        void WriteStartArray();
        void WriteEndArray();
        void WritePropertyName(string name);
        void WriteString(string value);
        void WriteNumber(long value);
        void WriteNumber(double value);
        void WriteBoolean(bool value);
        void WriteNull();

        /// <summary>Copies a parsed value, whichever codec parsed it.</summary>
        void WriteNode(IHaivenJsonNode value);
    }

    /// <summary>
    /// A payload type the SDK did not generate, writing itself. Implement it on a
    /// game's own payload class to send it under the built-in codec, which never
    /// reflects over a type to find its members.
    /// </summary>
    public interface IHaivenJsonWritable
    {
        void WriteJson(IHaivenJsonWriter writer);
    }

    /// <summary>
    /// The JSON library behind the SDK. <see cref="BuiltInJsonCodec"/> is the
    /// default; the Haiven.Sdk.SystemTextJson adapter is the alternative.
    /// </summary>
    public interface IHaivenJsonCodec
    {
        /// <summary>A short name for diagnostics, such as <c>builtin</c> or <c>system-text-json</c>.</summary>
        string Name { get; }

        /// <summary>Parses one JSON document; throws <see cref="HaivenJsonException"/> on malformed text.</summary>
        IHaivenJsonNode Parse(string json);

        /// <summary>Runs <paramref name="write"/> against a fresh writer and returns the text it wrote.</summary>
        string Write(Action<IHaivenJsonWriter> write);

        /// <summary>
        /// Writes a value of a type the SDK did not generate and that does not
        /// implement <see cref="IHaivenJsonWritable"/>. The built-in codec returns
        /// false, because the only general answer is reflection.
        /// </summary>
        bool TryWriteUnknown(IHaivenJsonWriter writer, object value);

        /// <summary>
        /// Reads a value of a type the SDK did not generate. The built-in codec
        /// returns false, for the reason <see cref="TryWriteUnknown"/> gives.
        /// </summary>
        bool TryReadUnknown(IHaivenJsonNode node, Type type, out object? value);
    }

    /// <summary>Malformed JSON, or JSON whose shape is not the wire type it was read as.</summary>
    public sealed class HaivenJsonException : FormatException
    {
        public HaivenJsonException(string message)
            : base(message)
        {
        }

        public HaivenJsonException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    /// <summary>The process-wide codec choice, and the entry points that use it.</summary>
    public static class HaivenJson
    {
        private static IHaivenJsonCodec codec = BuiltInJsonCodec.Instance;

        /// <summary>
        /// The codec used wherever one is not passed explicitly. Defaults to
        /// <see cref="BuiltInJsonCodec"/>. Set it once at startup, before the
        /// first session or REST client is created.
        /// </summary>
        public static IHaivenJsonCodec Default
        {
            get => Volatile.Read(ref codec);
            set => Volatile.Write(ref codec, value ?? throw new ArgumentNullException(nameof(value)));
        }

        public static IHaivenJsonNode Parse(string json) => Default.Parse(json);

        /// <summary>Serializes a value through <see cref="Default"/>; see <see cref="HaivenJsonWrite.Value"/> for what it accepts.</summary>
        public static string Serialize(object? value) => Serialize(value, Default);

        public static string Serialize(object? value, IHaivenJsonCodec codec)
        {
            if (codec is null) throw new ArgumentNullException(nameof(codec));
            return codec.Write(writer => HaivenJsonWrite.Value(writer, value, codec));
        }
    }

    /// <summary>
    /// Reads scalars and containers out of a node, checking the kind. Generated
    /// readers call these; <c>path</c> names the wire field for the error.
    /// </summary>
    public static class HaivenJsonRead
    {
        public static HaivenJsonException Missing(string path) =>
            new HaivenJsonException($"haiven: {path}: missing");

        public static HaivenJsonException Expected(string what, IHaivenJsonNode node, string path) =>
            new HaivenJsonException($"haiven: {path}: expected {what}, got {Describe(node.Kind)}");

        public static void ExpectObject(IHaivenJsonNode node, string path)
        {
            if (node.Kind != HaivenJsonKind.Object) throw Expected("an object", node, path);
        }

        public static bool IsNull(IHaivenJsonNode node) => node.Kind == HaivenJsonKind.Null;

        public static string String(IHaivenJsonNode node, string path)
        {
            if (node.Kind != HaivenJsonKind.String) throw Expected("a string", node, path);
            return node.GetString();
        }

        public static string? NullableString(IHaivenJsonNode node, string path) =>
            node.Kind == HaivenJsonKind.Null ? null : String(node, path);

        public static bool Boolean(IHaivenJsonNode node, string path)
        {
            switch (node.Kind)
            {
                case HaivenJsonKind.True: return true;
                case HaivenJsonKind.False: return false;
                default: throw Expected("a boolean", node, path);
            }
        }

        public static bool? NullableBoolean(IHaivenJsonNode node, string path) =>
            node.Kind == HaivenJsonKind.Null ? (bool?)null : Boolean(node, path);

        public static int Int32(IHaivenJsonNode node, string path)
        {
            if (node.Kind != HaivenJsonKind.Number
                || !int.TryParse(node.GetNumberText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                throw Expected("a 32-bit integer", node, path);
            }

            return value;
        }

        public static int? NullableInt32(IHaivenJsonNode node, string path) =>
            node.Kind == HaivenJsonKind.Null ? (int?)null : Int32(node, path);

        public static long Int64(IHaivenJsonNode node, string path)
        {
            if (node.Kind != HaivenJsonKind.Number
                || !long.TryParse(node.GetNumberText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                throw Expected("a 64-bit integer", node, path);
            }

            return value;
        }

        public static long? NullableInt64(IHaivenJsonNode node, string path) =>
            node.Kind == HaivenJsonKind.Null ? (long?)null : Int64(node, path);

        public static double Double(IHaivenJsonNode node, string path)
        {
            if (node.Kind != HaivenJsonKind.Number
                || !double.TryParse(node.GetNumberText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                || double.IsInfinity(value))
            {
                throw Expected("a number", node, path);
            }

            return value;
        }

        public static double? NullableDouble(IHaivenJsonNode node, string path) =>
            node.Kind == HaivenJsonKind.Null ? (double?)null : Double(node, path);

        /// <summary>An unconstrained value: kept as the parsed node, or null for JSON null.</summary>
        public static object? Any(IHaivenJsonNode node) =>
            node.Kind == HaivenJsonKind.Null ? null : node;

        public static T[] Array<T>(IHaivenJsonNode node, string path, Func<IHaivenJsonNode, string, T> item)
        {
            if (node.Kind != HaivenJsonKind.Array) throw Expected("an array", node, path);
            var items = new List<T>();
            foreach (var value in node.EnumerateArray()) items.Add(item(value, path + "[]"));
            return items.ToArray();
        }

        public static List<T> List<T>(IHaivenJsonNode node, string path, Func<IHaivenJsonNode, string, T> item)
        {
            if (node.Kind != HaivenJsonKind.Array) throw Expected("an array", node, path);
            var items = new List<T>();
            foreach (var value in node.EnumerateArray()) items.Add(item(value, path + "[]"));
            return items;
        }

        public static Dictionary<string, T> Map<T>(IHaivenJsonNode node, string path, Func<IHaivenJsonNode, string, T> item)
        {
            ExpectObject(node, path);
            var entries = new Dictionary<string, T>();
            foreach (var member in node.EnumerateObject()) entries[member.Key] = item(member.Value, path + "." + member.Key);
            return entries;
        }

        /// <summary>
        /// Reads a value whose type is only known at run time: a scalar, a parsed
        /// node, or a generated contract or REST type. Anything else goes to the
        /// codec's <see cref="IHaivenJsonCodec.TryReadUnknown"/>, and fails under
        /// the built-in codec, because the only general answer is reflection.
        /// </summary>
        public static object? Known(Type type, IHaivenJsonNode node, string path, IHaivenJsonCodec codec)
        {
            if (type is null) throw new ArgumentNullException(nameof(type));
            if (node is null) throw new ArgumentNullException(nameof(node));
            if (codec is null) throw new ArgumentNullException(nameof(codec));

            if (type == typeof(IHaivenJsonNode) || type == typeof(object)) return Any(node);
            if (type == typeof(string)) return NullableString(node, path);
            if (type == typeof(bool)) return Boolean(node, path);
            if (type == typeof(bool?)) return NullableBoolean(node, path);
            if (type == typeof(int)) return Int32(node, path);
            if (type == typeof(int?)) return NullableInt32(node, path);
            if (type == typeof(long)) return Int64(node, path);
            if (type == typeof(long?)) return NullableInt64(node, path);
            if (type == typeof(double)) return Double(node, path);
            if (type == typeof(double?)) return NullableDouble(node, path);
            if (ContractsJson.TryRead(type, node, out var value) || Rest.RestJson.TryRead(type, node, out value)) return value;
            if (codec.TryReadUnknown(node, type, out value)) return value;

            throw new NotSupportedException(
                $"haiven: the {codec.Name} JSON codec cannot read a {type.FullName} without reflection. "
                + "Decode a generated contract type, or opt into the System.Text.Json codec (Haiven.Sdk.SystemTextJson).");
        }

        /// <summary>A nullable reference read: null for JSON null, else <paramref name="read"/>.</summary>
        public static T? OrNull<T>(IHaivenJsonNode node, string path, Func<IHaivenJsonNode, string, T> read)
            where T : class =>
            node.Kind == HaivenJsonKind.Null ? null : read(node, path);

        private static string Describe(HaivenJsonKind kind)
        {
            switch (kind)
            {
                case HaivenJsonKind.Null: return "null";
                case HaivenJsonKind.False:
                case HaivenJsonKind.True: return "a boolean";
                case HaivenJsonKind.Number: return "a number";
                case HaivenJsonKind.String: return "a string";
                case HaivenJsonKind.Array: return "an array";
                default: return "an object";
            }
        }
    }

    /// <summary>Writes values whose type is only known at run time, such as a session payload.</summary>
    public static class HaivenJsonWrite
    {
        /// <summary>
        /// Writes <paramref name="value"/> without reflection: null, strings,
        /// booleans, numbers, a parsed node, an <see cref="IHaivenJsonWritable"/>,
        /// any generated contract or REST type, a string-keyed dictionary, or a
        /// sequence of those. Anything else goes to the codec's
        /// <see cref="IHaivenJsonCodec.TryWriteUnknown"/>, and fails under the
        /// built-in codec with the remedy in the message.
        /// </summary>
        public static void Value(IHaivenJsonWriter writer, object? value, IHaivenJsonCodec codec)
        {
            switch (value)
            {
                case null: writer.WriteNull(); return;
                case string text: writer.WriteString(text); return;
                case bool flag: writer.WriteBoolean(flag); return;
                case int number: writer.WriteNumber(number); return;
                case long number: writer.WriteNumber(number); return;
                case short number: writer.WriteNumber(number); return;
                case byte number: writer.WriteNumber(number); return;
                case sbyte number: writer.WriteNumber(number); return;
                case ushort number: writer.WriteNumber(number); return;
                case uint number: writer.WriteNumber(number); return;
                case double number: writer.WriteNumber(number); return;
                case float number: writer.WriteNumber(number); return;
                case IHaivenJsonNode node: writer.WriteNode(node); return;
                case IHaivenJsonWritable writable: writable.WriteJson(writer); return;
            }

            if (ContractsJson.TryWrite(writer, value, codec) || Rest.RestJson.TryWrite(writer, value)) return;

            if (value is IDictionary dictionary && IsStringKeyed(dictionary))
            {
                writer.WriteStartObject();
                foreach (DictionaryEntry entry in dictionary)
                {
                    writer.WritePropertyName((string)entry.Key);
                    Value(writer, entry.Value, codec);
                }

                writer.WriteEndObject();
                return;
            }

            if (value is IEnumerable sequence)
            {
                writer.WriteStartArray();
                foreach (var item in sequence) Value(writer, item, codec);
                writer.WriteEndArray();
                return;
            }

            if (codec.TryWriteUnknown(writer, value)) return;

            throw new NotSupportedException(
                $"haiven: the {codec.Name} JSON codec cannot write a {value.GetType().FullName} without reflection. "
                + "Send a generated contract type, a parsed node, a dictionary, or a type implementing IHaivenJsonWritable; "
                + "or opt into the System.Text.Json codec (Haiven.Sdk.SystemTextJson).");
        }

        private static bool IsStringKeyed(IDictionary dictionary)
        {
            foreach (var key in dictionary.Keys)
            {
                if (!(key is string)) return false;
            }

            return true;
        }
    }
}
