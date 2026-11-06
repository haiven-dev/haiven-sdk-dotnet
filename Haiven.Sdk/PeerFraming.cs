using System;
using System.Collections.Generic;

namespace Haiven.Sdk
{
    /// <summary>
    /// How a game networking layer's messages travel over the two data channels (ADR 0036 point 20).
    ///
    /// Unreliable messages carry a one-byte header that names the delivery.
    /// <c>UnreliableSequenced</c> adds a 16-bit sequence number, so its header is
    /// three bytes. Reliable messages go into one length-prefixed stream that is
    /// cut into data channel messages of at most 64 KiB; the reliable channel is
    /// ordered, so the receiver only has to join the pieces back together.
    /// </summary>
    public static class PeerFraming
    {
        public const byte Unreliable = 0;
        public const byte UnreliableSequenced = 1;

        /// <summary>
        /// Sent on the unreliable channel when a side closes on purpose. Through
        /// TURN the closing side's allocation goes with its connection, so without
        /// it the other side learns only when ICE fails, about 15 seconds later.
        /// </summary>
        public const byte Goodbye = 2;
        public const int SequencedHeader = 3;
        public const int LengthPrefix = 4;

        /// <summary>
        /// The largest reliable game message. A longer one cannot pass the 1 MiB
        /// send queue, so a peer that declares one is not following this framing.
        /// </summary>
        public const int MaxReliablePayload = (int)HaivenPeerLink.MaxSendQueue - LengthPrefix;

        /// <summary>The largest game message that fits an unreliable message with the largest header.</summary>
        public const int MaxUnreliablePayload = HaivenPeerLink.MaxUnreliableMessage - SequencedHeader;

        public static byte[] EncodeUnreliable(ArraySegment<byte> payload)
        {
            var message = new byte[payload.Count + 1];
            message[0] = Unreliable;
            Buffer.BlockCopy(payload.Array!, payload.Offset, message, 1, payload.Count);
            return message;
        }

        public static byte[] EncodeSequenced(ArraySegment<byte> payload, ushort sequence)
        {
            var message = new byte[payload.Count + SequencedHeader];
            message[0] = UnreliableSequenced;
            message[1] = (byte)(sequence >> 8);
            message[2] = (byte)sequence;
            Buffer.BlockCopy(payload.Array!, payload.Offset, message, SequencedHeader, payload.Count);
            return message;
        }

        /// <summary>
        /// True when <paramref name="sequence"/> is newer than <paramref name="last"/>
        /// under 16-bit serial number arithmetic (RFC 1982), which survives wraparound.
        /// </summary>
        public static bool IsNewer(ushort sequence, ushort last)
        {
            var distance = (ushort)(sequence - last);
            return distance != 0 && distance < 0x8000;
        }

        /// <summary>One game message as data channel messages of at most 64 KiB each.</summary>
        public static List<byte[]> EncodeReliable(ArraySegment<byte> payload)
        {
            var framed = new byte[LengthPrefix + payload.Count];
            framed[0] = (byte)(payload.Count >> 24);
            framed[1] = (byte)(payload.Count >> 16);
            framed[2] = (byte)(payload.Count >> 8);
            framed[3] = (byte)payload.Count;
            Buffer.BlockCopy(payload.Array!, payload.Offset, framed, LengthPrefix, payload.Count);

            var chunks = new List<byte[]>();
            for (var offset = 0; offset < framed.Length; offset += HaivenPeerLink.MaxReliableMessage)
            {
                var length = Math.Min(HaivenPeerLink.MaxReliableMessage, framed.Length - offset);
                var chunk = new byte[length];
                Buffer.BlockCopy(framed, offset, chunk, 0, length);
                chunks.Add(chunk);
            }
            return chunks;
        }
    }

    /// <summary>Joins the reliable stream back into whole game messages.</summary>
    public sealed class ReliableAssembler
    {
        private byte[] buffer = new byte[PeerFraming.LengthPrefix];
        private int filled;
        private int expected = -1;

        /// <summary>Adds one data channel message and returns every game message it completes.</summary>
        public List<byte[]> Add(byte[] chunk)
        {
            var complete = new List<byte[]>();
            var read = 0;
            while (read < chunk.Length)
            {
                var want = (expected < 0 ? PeerFraming.LengthPrefix : expected) - filled;
                var take = Math.Min(want, chunk.Length - read);
                Buffer.BlockCopy(chunk, read, buffer, filled, take);
                filled += take;
                read += take;
                if (filled < (expected < 0 ? PeerFraming.LengthPrefix : expected)) break;

                if (expected < 0)
                {
                    expected = (buffer[0] << 24) | (buffer[1] << 16) | (buffer[2] << 8) | buffer[3];
                    // Checked before allocating: the length comes from the peer.
                    if (expected < 0 || expected > PeerFraming.MaxReliablePayload)
                        throw new InvalidOperationException(
                            $"reliable message length {(uint)expected} is above {PeerFraming.MaxReliablePayload}");
                    buffer = new byte[expected];
                    filled = 0;
                    if (expected > 0) continue;
                }

                complete.Add(buffer);
                buffer = new byte[PeerFraming.LengthPrefix];
                filled = 0;
                expected = -1;
            }
            return complete;
        }
    }
}
