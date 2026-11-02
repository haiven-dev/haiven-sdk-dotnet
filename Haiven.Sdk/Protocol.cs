// The /v0/ws envelope, bound in C# against the surface Unity supports.
//
// The question this file exists to answer is narrow: the two outbound shapes are
// discriminated by the *presence* of `success`, not by its value (F10). C# has
// no undefined, and every JSON reader in the ecosystem has an opinion about what
// a missing field becomes. Whether that discriminant survives the crossing is
// the thing worth finding out before three more bindings are written against it.

using System.Text.Json;

namespace Haiven.Sdk
{
    /// <summary>What a client sends.</summary>
    public sealed class ClientFrame
    {
        public string Type { get; set; } = "";

        /// <summary>
        /// Always send one. The server mints a UUID when this is absent, which
        /// the client never learns, so the reply arrives correlated to an id it
        /// cannot match.
        /// </summary>
        public string RequestId { get; set; } = "";

        public object? Payload { get; set; }
    }

    /// <summary>
    /// A frame off the wire, before it is known which of the two outbound shapes
    /// it is.
    /// </summary>
    public sealed class ServerFrame
    {
        public string Type { get; private set; } = "";

        /// <summary>
        /// The id the client sent, or empty when the server initiated the frame.
        /// Absent entirely on an event.
        /// </summary>
        public string? RequestId { get; private set; }

        /// <summary>
        /// Nullable on purpose, and this is the whole finding.
        ///
        /// A reply always carries this field and an event never does, so the
        /// discriminant is presence rather than value. A non-nullable bool would
        /// map an absent field to <c>false</c> and make every event look like a
        /// failed reply — the reader has to be able to say "not present", which
        /// is what <c>bool?</c> buys and what a value type cannot.
        /// </summary>
        public bool? Success { get; private set; }

        public JsonElement? Payload { get; private set; }

        /// <summary>A code from the published vocabulary, never prose.</summary>
        public string? Error { get; private set; }

        /// <summary>
        /// True when this frame answers something the client sent.
        ///
        /// Reads the presence of <see cref="Success"/> rather than its value: a
        /// refused request is still a reply, and <c>success: false</c> must not
        /// be mistaken for an event.
        /// </summary>
        public bool IsReply => Success.HasValue;

        /// <summary>
        /// Parses a frame while preserving which fields were actually present.
        ///
        /// Hand-rolled against <see cref="JsonDocument"/> rather than
        /// deserialised into a POCO. An attribute-driven deserialiser has to be
        /// told, per field, that "missing" is distinct from "default", and the
        /// one built into Unity does not offer that distinction at all — so the
        /// reader that survives every target is the one that asks the document
        /// what it contains.
        /// </summary>
        public static ServerFrame Parse(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var frame = new ServerFrame();

            if (root.TryGetProperty("type", out var type))
            {
                frame.Type = type.GetString() ?? "";
            }

            if (root.TryGetProperty("requestId", out var requestId))
            {
                frame.RequestId = requestId.GetString();
            }

            if (root.TryGetProperty("success", out var success))
            {
                frame.Success = success.GetBoolean();
            }

            if (root.TryGetProperty("error", out var error))
            {
                frame.Error = error.GetString();
            }

            if (root.TryGetProperty("payload", out var payload))
            {
                frame.Payload = payload.Clone();
            }

            return frame;
        }
    }
}
