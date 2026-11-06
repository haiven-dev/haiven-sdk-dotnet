using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haiven.Sdk.Json.SystemTextJson
{
    /// <summary>
    /// Haiven.Sdk's JSON seam over System.Text.Json: <see cref="JsonDocument"/>
    /// to parse, <see cref="Utf8JsonWriter"/> to write.
    ///
    /// The generated readers and writers do the same work under this codec as
    /// under the built-in one. What this codec adds is a fallback for types the
    /// SDK did not generate: a game's own payload class is serialized
    /// reflectively, with the camelCase, omit-null options the SDK used before
    /// it had a codec. That fallback is the one part that is not trim- or
    /// AOT-safe, and it only runs for types the SDK cannot name.
    /// </summary>
    public sealed class SystemTextJsonCodec : IHaivenJsonCodec
    {
        public static readonly SystemTextJsonCodec Instance = new SystemTextJsonCodec();

        private SystemTextJsonCodec()
        {
        }

        /// <summary>The options the reflective fallback uses: the SDK's wire spelling.</summary>
        public static JsonSerializerOptions FallbackOptions { get; } = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public string Name => "system-text-json";

        /// <summary>Makes this codec <see cref="HaivenJson.Default"/>.</summary>
        public static void Install() => HaivenJson.Default = Instance;

        public IHaivenJsonNode Parse(string json)
        {
            if (json is null) throw new ArgumentNullException(nameof(json));
            try
            {
                using (var document = JsonDocument.Parse(json))
                {
                    return new SystemTextJsonNode(document.RootElement.Clone());
                }
            }
            catch (JsonException error)
            {
                throw new HaivenJsonException("haiven: malformed JSON: " + error.Message, error);
            }
        }

        public string Write(Action<IHaivenJsonWriter> write)
        {
            if (write is null) throw new ArgumentNullException(nameof(write));
            using (var stream = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(stream))
                {
                    write(new SystemTextJsonWriter(writer));
                }

                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public bool TryWriteUnknown(IHaivenJsonWriter writer, object value)
        {
            if (writer is null) throw new ArgumentNullException(nameof(writer));
            if (value is null) throw new ArgumentNullException(nameof(value));
            writer.WriteNode(Parse(JsonSerializer.Serialize(value, value.GetType(), FallbackOptions)));
            return true;
        }

        public bool TryReadUnknown(IHaivenJsonNode node, Type type, out object? value)
        {
            if (node is null) throw new ArgumentNullException(nameof(node));
            if (type is null) throw new ArgumentNullException(nameof(type));
            try
            {
                value = JsonSerializer.Deserialize(node.ToJson(), type, FallbackOptions);
            }
            catch (JsonException error)
            {
                throw new HaivenJsonException($"haiven: cannot read a {type.FullName}: {error.Message}", error);
            }

            return true;
        }
    }

    /// <summary>A parsed value from <see cref="SystemTextJsonCodec"/>, over a detached <see cref="JsonElement"/>.</summary>
    public sealed class SystemTextJsonNode : IHaivenJsonNode
    {
        public SystemTextJsonNode(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Undefined) throw new ArgumentException("haiven: an undefined JsonElement", nameof(element));
            Element = element;
        }

        public JsonElement Element { get; }

        public HaivenJsonKind Kind
        {
            get
            {
                switch (Element.ValueKind)
                {
                    case JsonValueKind.Object: return HaivenJsonKind.Object;
                    case JsonValueKind.Array: return HaivenJsonKind.Array;
                    case JsonValueKind.String: return HaivenJsonKind.String;
                    case JsonValueKind.Number: return HaivenJsonKind.Number;
                    case JsonValueKind.True: return HaivenJsonKind.True;
                    case JsonValueKind.False: return HaivenJsonKind.False;
                    default: return HaivenJsonKind.Null;
                }
            }
        }

        public bool TryGetProperty(string name, out IHaivenJsonNode value)
        {
            if (Element.ValueKind == JsonValueKind.Object && Element.TryGetProperty(name, out var member))
            {
                value = new SystemTextJsonNode(member);
                return true;
            }

            value = HaivenJsonValue.Null;
            return false;
        }

        public IEnumerable<KeyValuePair<string, IHaivenJsonNode>> EnumerateObject()
        {
            if (Element.ValueKind != JsonValueKind.Object) yield break;
            foreach (var member in Element.EnumerateObject())
            {
                yield return new KeyValuePair<string, IHaivenJsonNode>(member.Name, new SystemTextJsonNode(member.Value));
            }
        }

        public IEnumerable<IHaivenJsonNode> EnumerateArray()
        {
            if (Element.ValueKind != JsonValueKind.Array) yield break;
            foreach (var item in Element.EnumerateArray()) yield return new SystemTextJsonNode(item);
        }

        public string GetString() =>
            Element.ValueKind == JsonValueKind.String
                ? Element.GetString()!
                : throw new HaivenJsonException($"haiven: expected a string, got {Kind}");

        public string GetNumberText() =>
            Element.ValueKind == JsonValueKind.Number
                ? Element.GetRawText()
                : throw new HaivenJsonException($"haiven: expected a number, got {Kind}");

        public string ToJson() => Element.GetRawText();

        public override string ToString() => ToJson();
    }

    /// <summary>The adapter's writer, over <see cref="Utf8JsonWriter"/>.</summary>
    public sealed class SystemTextJsonWriter : IHaivenJsonWriter
    {
        private readonly Utf8JsonWriter writer;

        public SystemTextJsonWriter(Utf8JsonWriter writer)
        {
            this.writer = writer ?? throw new ArgumentNullException(nameof(writer));
        }

        public void WriteStartObject() => writer.WriteStartObject();

        public void WriteEndObject() => writer.WriteEndObject();

        public void WriteStartArray() => writer.WriteStartArray();

        public void WriteEndArray() => writer.WriteEndArray();

        public void WritePropertyName(string name) => writer.WritePropertyName(name);

        public void WriteString(string value) => writer.WriteStringValue(value);

        public void WriteNumber(long value) => writer.WriteNumberValue(value);

        public void WriteNumber(double value) => writer.WriteNumberValue(value);

        public void WriteBoolean(bool value) => writer.WriteBooleanValue(value);

        public void WriteNull() => writer.WriteNullValue();

        public void WriteNode(IHaivenJsonNode value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            if (value is SystemTextJsonNode native)
            {
                native.Element.WriteTo(writer);
                return;
            }

            switch (value.Kind)
            {
                case HaivenJsonKind.Null: writer.WriteNullValue(); break;
                case HaivenJsonKind.True: writer.WriteBooleanValue(true); break;
                case HaivenJsonKind.False: writer.WriteBooleanValue(false); break;
                case HaivenJsonKind.String: writer.WriteStringValue(value.GetString()); break;
                case HaivenJsonKind.Number: writer.WriteRawValue(value.GetNumberText()); break;
                case HaivenJsonKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray()) WriteNode(item);
                    writer.WriteEndArray();
                    break;
                default:
                    writer.WriteStartObject();
                    foreach (var member in value.EnumerateObject())
                    {
                        writer.WritePropertyName(member.Key);
                        WriteNode(member.Value);
                    }

                    writer.WriteEndObject();
                    break;
            }
        }
    }

    /// <summary>Bridges for code that still wants System.Text.Json types.</summary>
    public static class SystemTextJsonExtensions
    {
        /// <summary>The node as a <see cref="JsonElement"/>, without a reparse when this codec produced it.</summary>
        public static JsonElement ToJsonElement(this IHaivenJsonNode node)
        {
            if (node is null) throw new ArgumentNullException(nameof(node));
            if (node is SystemTextJsonNode native) return native.Element;
            using (var document = JsonDocument.Parse(node.ToJson()))
            {
                return document.RootElement.Clone();
            }
        }
    }
}
