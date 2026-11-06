using System;
using System.Collections.Generic;
using System.Diagnostics;
using Haiven.Sdk.Contracts;
using Haiven.Sdk.Json;

namespace Haiven.Sdk
{
    /// <summary>
    /// WebRTC signaling over the Haiven game socket, as ADR 0036 points 14 and
    /// 15 describe: <c>p2p.signal</c> to one member, relayed by haiven-api with
    /// the sender's user id set by the server, acknowledged with
    /// <c>p2p.signal.ack</c>.
    ///
    /// The server only accepts a message for delivery, so reliability is here.
    /// An unacknowledged message is sent again 1, 2 and 4 seconds apart, and
    /// abandoned 4 seconds after the last copy. The
    /// receiver acknowledges every copy, drops duplicates by attempt and
    /// sequence, and hands messages on in sequence order. One attempt is one
    /// connection: the guest starts it with its offer, and the host answers
    /// within it.
    ///
    /// Each attempt carries the guest's <c>participationId</c> and an
    /// <c>epoch</c>, one higher for every offer the guest makes. The host
    /// starts a new attempt only for a higher epoch than the last it accepted
    /// from that participation, so a delayed offer cannot replace a newer one
    /// (SDK-P2P-8). A description is cut into fragments whose serialized
    /// <c>data</c> fits <see cref="MaxSignalData"/>, at most
    /// <see cref="MaxFragments"/> of them and <see cref="MaxDescriptionBytes"/>
    /// in all, and the receiver hands it on once every fragment is in.
    ///
    /// A closed game socket is the session's to resume (SDK-P2P-5), so it is
    /// not reported as an unreachable host. Retries wait while the socket is
    /// closed, and <see cref="Rebind"/> sends what is still unacknowledged on
    /// the resumed one. A session that cannot resume ends through
    /// <see cref="HaivenP2PSession.Ended"/>.
    ///
    /// Some replies are not a lost message. <c>rate_limited</c> sends the
    /// message again at the next retry without spending one (SDK-P2P-7).
    /// <c>invalid_payload</c> would be refused again, so the message is dropped
    /// and reported through <see cref="Refused"/>, as is a message too large to
    /// send at all.
    /// </summary>
    public sealed class HaivenSessionSignaling : IHaivenPeerSignaling, IHaivenPeerSignalingLifecycle
    {
        private static readonly double[] RetryDelays = { 1, 2, 4 };

        /// <summary>The most UTF-8 bytes haiven-api accepts in <c>data</c>, measured as a serialized JSON string.</summary>
        public const int MaxSignalData = 4096;

        /// <summary>The most fragments one offer or answer is cut into.</summary>
        public const int MaxFragments = 16;

        /// <summary>The largest SDP, in UTF-8 bytes, that is sent at all.</summary>
        public const int MaxDescriptionBytes = 32 * 1024;

        private HaivenSessionChannel channel;
        private readonly string sessionId;
        private readonly string participationId;
        private readonly Func<double> clock;

        // The guest's last epoch towards each peer, and on the host the highest
        // epoch accepted from each guest participation. They outlive Stop, so a
        // transport that restarts does not reuse an epoch.
        private readonly Dictionary<string, int> epochs = new Dictionary<string, int>();
        private readonly Dictionary<string, int> accepted = new Dictionary<string, int>();
        private readonly Dictionary<string, Outgoing> outgoing = new Dictionary<string, Outgoing>();
        private readonly Dictionary<string, Incoming> incoming = new Dictionary<string, Incoming>();
        private readonly List<Unacknowledged> unacknowledged = new List<Unacknowledged>();
        private bool running;
        private bool socketClosed;

        /// <param name="channel">The game socket the participation is bound to.</param>
        /// <param name="sessionId">The session both sides are members of.</param>
        /// <param name="localUserId">This member's user id.</param>
        /// <param name="hostUserId">The host's user id on a guest; null on the host.</param>
        /// <param name="clock">Seconds from any fixed point; a monotonic clock when null.</param>
        /// <param name="participationId">This member's participation; a guest's offers carry it so the host keys epochs by it.</param>
        public HaivenSessionSignaling(
            HaivenSessionChannel channel, string sessionId, string localUserId, string? hostUserId, Func<double>? clock = null,
            string? participationId = null)
        {
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            this.participationId = participationId ?? "";
            LocalUserId = localUserId ?? "";
            HostUserId = hostUserId;
            this.clock = clock ?? MonotonicSeconds();
        }

        public string LocalUserId { get; }

        public string? HostUserId { get; }

        public event Action<string, PeerSignal>? Received;

        /// <summary>Never raised: the session resumes a closed socket or ends.</summary>
        public event Action<string>? HostUnreachable { add { } remove { } }

        /// <summary>A message was never acknowledged after every retry: the recipient and the sequence number.</summary>
        public event Action<string, int>? Abandoned;

        /// <summary>
        /// A message that cannot be delivered and was not retried: the recipient
        /// and why. <c>sdp_too_large</c> when its <c>data</c> is above
        /// <see cref="MaxSignalData"/> and it was never sent; <c>invalid_payload</c>
        /// when the server refused it. The connection attempt it belonged to cannot complete.
        /// </summary>
        public event Action<string, string>? Refused;

        /// <summary>Messages sent and not yet acknowledged.</summary>
        public int PendingAcknowledgements => unacknowledged.Count;

        // The session starts signaling when it enters, before the game starts
        // its networking, so a second start finds it running and succeeds.
        public bool Start(bool asHost)
        {
            if (running) return true;
            running = true;
            channel.Event += OnEvent;
            channel.Lost += OnLost;
            return true;
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            socketClosed = false;
            channel.Event -= OnEvent;
            channel.Lost -= OnLost;
            outgoing.Clear();
            incoming.Clear();
            unacknowledged.Clear();
        }

        /// <summary>
        /// Moves to the socket the session resumed on (SDK-P2P-5). Messages not yet
        /// acknowledged go out again on it at once, since the old socket may have lost them.
        /// </summary>
        public void Rebind(HaivenSessionChannel resumed)
        {
            if (resumed is null) throw new ArgumentNullException(nameof(resumed));
            if (resumed == channel) return;
            if (running)
            {
                channel.Event -= OnEvent;
                channel.Lost -= OnLost;
                resumed.Event += OnEvent;
                resumed.Lost += OnLost;
            }
            channel = resumed;
            socketClosed = false;
            var now = clock();
            foreach (var message in unacknowledged)
            {
                message.NextTry = now;
                message.Retries = Math.Min(message.Retries, RetryDelays.Length - 1);
            }
        }

        public void Poll()
        {
            channel.Pump();
            if (socketClosed) return;
            var now = clock();
            for (var i = unacknowledged.Count - 1; i >= 0; i--)
            {
                var message = unacknowledged[i];
                if (now < message.NextTry) continue;
                if (message.Retries >= RetryDelays.Length)
                {
                    unacknowledged.RemoveAt(i);
                    Abandoned?.Invoke(message.ToUserId, message.Seq);
                    continue;
                }
                message.Retries++;
                message.NextTry = now + RetryDelays[Math.Min(message.Retries, RetryDelays.Length - 1)];
                Write(message);
            }
        }

        public void Send(string toUserId, PeerSignal signal)
        {
            if (!running) return;
            if (signal is null) throw new ArgumentNullException(nameof(signal));

            // An offer starts a new attempt with the next epoch: one per
            // connection, including a guest that offers again. The host's answer
            // and candidates use the attempt and epoch of the offer it answers.
            var startsAttempt = !outgoing.TryGetValue(toUserId, out var attempt) || signal.Kind == PeerSignalKind.Offer;
            if (startsAttempt)
            {
                if (signal.Kind == PeerSignalKind.Offer)
                {
                    var epoch = (epochs.TryGetValue(toUserId, out var last) ? last : 0) + 1;
                    attempt = new Outgoing(Guid.NewGuid().ToString("D"), participationId, epoch);
                }
                else if (incoming.TryGetValue(toUserId, out var theirs))
                {
                    attempt = new Outgoing(theirs.AttemptId, theirs.ParticipationId, theirs.Epoch);
                }
                else
                {
                    attempt = new Outgoing(Guid.NewGuid().ToString("D"), participationId, epochs.TryGetValue(toUserId, out var last) ? last : 0);
                }
            }

            var fragments = Fragments(signal, attempt!.ParticipationId, attempt.Epoch);
            if (fragments == null)
            {
                // haiven-api would refuse it every time, so it is not sent (SDK-P2P-8).
                Refused?.Invoke(toUserId, "sdp_too_large");
                return;
            }
            if (startsAttempt)
            {
                if (signal.Kind == PeerSignalKind.Offer) epochs[toUserId] = attempt.Epoch;
                outgoing[toUserId] = attempt;
                unacknowledged.RemoveAll(message => message.ToUserId == toUserId);
            }

            foreach (var data in fragments)
            {
                var sent = new Unacknowledged(toUserId, attempt.AttemptId, ++attempt.LastSeq, data)
                {
                    NextTry = clock() + RetryDelays[0],
                };
                unacknowledged.Add(sent);
                Write(sent);
            }
        }

        private void Write(Unacknowledged message)
        {
            channel.Send(WsTypes.P2pSignal, new P2pSignalV0
            {
                SessionId = sessionId,
                ToUserId = message.ToUserId,
                AttemptId = message.AttemptId,
                Seq = message.Seq,
                Data = message.Data,
            }, reply =>
            {
                if (reply.Success != false) return;
                switch (reply.Error)
                {
                    case "peer_not_in_session":
                        // The member left, so there is nobody to retry for.
                        unacknowledged.Remove(message);
                        break;
                    case "invalid_payload":
                        if (unacknowledged.Remove(message)) Refused?.Invoke(message.ToUserId, "invalid_payload");
                        break;
                    case "rate_limited":
                        // Not lost: the next retry sends it again and this copy does not count.
                        message.Retries = Math.Max(message.Retries - 1, -1);
                        break;
                }
            });
        }

        /// <summary>
        /// The UTF-8 size of <paramref name="data"/> as haiven-api measures it:
        /// a JSON string as Go's encoder writes it, quotes included, with
        /// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, U+2028 and U+2029 escaped as six bytes.
        /// </summary>
        public static int SerializedSize(string data)
        {
            if (data is null) throw new ArgumentNullException(nameof(data));
            var size = 2;
            for (var i = 0; i < data.Length; i++)
            {
                var c = data[i];
                if (c == '"' || c == '\\' || c == '\n' || c == '\r' || c == '\t') size += 2;
                else if (c < 0x20 || c == '<' || c == '>' || c == '&' || c == (char)0x2028 || c == (char)0x2029) size += 6;
                else if (c < 0x80) size += 1;
                else if (c < 0x800) size += 2;
                else if (char.IsHighSurrogate(c) && i + 1 < data.Length && char.IsLowSurrogate(data[i + 1]))
                {
                    size += 4;
                    i++;
                }
                else size += 3;
            }
            return size;
        }

        private void OnEvent(ServerFrame frame)
        {
            if (!running || frame.Payload == null) return;
            try
            {
                if (frame.Type == WsTypes.P2pSignalAck)
                    OnAck(ContractsJson.ReadP2pSignalAckEventV0(frame.Payload, "payload"));
                else if (frame.Type == WsTypes.P2pSignalReceived)
                    OnSignal(ContractsJson.ReadP2pSignalReceivedV0(frame.Payload, "payload"));
            }
            catch (HaivenJsonException)
            {
                // A malformed event is the server's bug; the retries cover what it carried.
            }
        }

        private void OnAck(P2pSignalAckEventV0 ack)
        {
            if (ack.SessionId != sessionId) return;
            unacknowledged.RemoveAll(message =>
                message.ToUserId == ack.FromUserId && message.AttemptId == ack.AttemptId && message.Seq == ack.Seq);
        }

        private void OnSignal(P2pSignalReceivedV0 received)
        {
            if (received.SessionId != sessionId) return;
            var from = received.FromUserId;
            var attemptId = received.AttemptId;
            var seq = received.Seq;
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(attemptId) || seq < 1) return;

            // Every copy is acknowledged, since the first acknowledgement may be the one lost.
            channel.Send(WsTypes.P2pSignalAck, new P2pSignalAckV0
            {
                SessionId = sessionId,
                ToUserId = from,
                AttemptId = attemptId,
                Seq = seq,
            });

            if (!incoming.TryGetValue(from, out var attempt) || attempt.AttemptId != attemptId)
            {
                // A new attempt is either the host answering the attempt this
                // side started, or, on the host, a guest's newer attempt. Any of
                // its messages can come first, a candidate before the offer
                // included, so the epoch decides rather than the kind.
                var first = TryRead(received.Data);
                if (first == null) return;
                var ours = outgoing.TryGetValue(from, out var mine) && mine.AttemptId == attemptId;
                if (!ours)
                {
                    if (HostUserId != null) return;
                    var key = from + "\n" + first.ParticipationId;
                    if (first.Epoch < 1 || (accepted.TryGetValue(key, out var highest) && first.Epoch <= highest)) return;
                    accepted[key] = first.Epoch;
                    // The guest started over: what this side sent in the old attempt is void.
                    outgoing.Remove(from);
                    unacknowledged.RemoveAll(message => message.ToUserId == from);
                }
                attempt = new Incoming(attemptId, first.ParticipationId, first.Epoch);
                incoming[from] = attempt;
            }

            if (seq < attempt.NextSeq || attempt.Held.ContainsKey(seq)) return;
            attempt.Held[seq] = received.Data;
            while (attempt.Held.TryGetValue(attempt.NextSeq, out var next))
            {
                attempt.Held.Remove(attempt.NextSeq);
                attempt.NextSeq++;
                var piece = TryRead(next);
                var signal = piece == null ? null : attempt.Take(piece);
                if (signal != null) Received?.Invoke(from, signal);
            }
        }

        private void OnLost() => socketClosed = true;

        /// <summary>
        /// The <c>data</c> values for one signal, opaque to the server: its kind,
        /// the guest's <c>participationId</c>, the attempt's <c>epoch</c>, and for
        /// a description its <c>part</c> of <c>parts</c>. Null when it cannot be
        /// sent: a description above <see cref="MaxDescriptionBytes"/> or one that
        /// needs more than <see cref="MaxFragments"/> fragments.
        /// </summary>
        internal static List<string>? Fragments(PeerSignal signal, string participationId, int epoch)
        {
            if (signal.Kind == PeerSignalKind.Candidate)
            {
                var data = Wrap("candidate", participationId, epoch);
                data["candidate"] = signal.Candidate!.Candidate;
                data["sdpMid"] = signal.Candidate.SdpMid;
                data["sdpMLineIndex"] = signal.Candidate.SdpMLineIndex;
                var text = HaivenJson.Serialize(data);
                return SerializedSize(text) <= MaxSignalData ? new List<string> { text } : null;
            }

            var kind = signal.Kind == PeerSignalKind.Offer ? "offer" : "answer";
            var sdp = signal.Sdp ?? "";
            if (System.Text.Encoding.UTF8.GetByteCount(sdp) > MaxDescriptionBytes) return null;
            var whole = Description(kind, participationId, epoch, 1, 1, sdp);
            if (SerializedSize(whole) <= MaxSignalData) return new List<string> { whole };

            // What each character of the SDP adds is the same wherever it falls,
            // so the room left beside the widest wrapper bounds every fragment.
            var room = MaxSignalData - SerializedSize(Description(kind, participationId, epoch, MaxFragments, MaxFragments, ""));
            // The serialized SDP sits inside the data string, so its own quotes
            // are escaped there too: a character costs its size beyond the "" around it.
            var quotes = SerializedSize(HaivenJson.Serialize(""));
            var pieces = new List<string>();
            var start = 0;
            while (start < sdp.Length)
            {
                var used = 0;
                var end = start;
                while (end < sdp.Length)
                {
                    var width = char.IsHighSurrogate(sdp[end]) && end + 1 < sdp.Length && char.IsLowSurrogate(sdp[end + 1]) ? 2 : 1;
                    var cost = SerializedSize(HaivenJson.Serialize(sdp.Substring(end, width))) - quotes;
                    if (used + cost > room) break;
                    used += cost;
                    end += width;
                }
                if (end == start || pieces.Count == MaxFragments) return null;
                pieces.Add(sdp.Substring(start, end - start));
                start = end;
            }
            var fragments = new List<string>();
            for (var i = 0; i < pieces.Count; i++)
                fragments.Add(Description(kind, participationId, epoch, i + 1, pieces.Count, pieces[i]));
            return fragments;
        }

        private static Dictionary<string, object?> Wrap(string kind, string participationId, int epoch) =>
            new Dictionary<string, object?>
            {
                ["kind"] = kind,
                ["participationId"] = participationId,
                ["epoch"] = epoch,
            };

        private static string Description(string kind, string participationId, int epoch, int part, int parts, string sdp)
        {
            var data = Wrap(kind, participationId, epoch);
            data["part"] = part;
            data["parts"] = parts;
            data["sdp"] = sdp;
            return HaivenJson.Serialize(data);
        }

        /// <summary>One <c>data</c> value as read; null when it is not JSON or not a kind this side knows.</summary>
        private static Envelope? TryRead(string data)
        {
            IHaivenJsonNode node;
            try
            {
                node = HaivenJson.Parse(data);
            }
            catch (HaivenJsonException)
            {
                return null;
            }
            var kind = Text(node, "kind");
            var envelope = new Envelope(Text(node, "participationId") ?? "", Number(node, "epoch") ?? 0);
            switch (kind)
            {
                case "offer":
                case "answer":
                    envelope.Description = kind;
                    envelope.Part = Number(node, "part") ?? 1;
                    envelope.Parts = Number(node, "parts") ?? 1;
                    envelope.Sdp = Text(node, "sdp") ?? "";
                    if (envelope.Part < 1 || envelope.Parts < 1 || envelope.Part > envelope.Parts || envelope.Parts > MaxFragments) return null;
                    return envelope;
                case "candidate":
                    envelope.Candidate = new PeerIceCandidate(Text(node, "candidate") ?? "", Text(node, "sdpMid"), Number(node, "sdpMLineIndex"));
                    return envelope;
                default:
                    return null;
            }
        }

        private static int? Number(IHaivenJsonNode node, string name) =>
            node.Kind == HaivenJsonKind.Object && node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.Number
                && int.TryParse(value.GetNumberText(), out var parsed)
                    ? parsed
                    : (int?)null;

        private static string? Text(IHaivenJsonNode node, string name) =>
            node.Kind == HaivenJsonKind.Object && node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.String
                ? value.GetString()
                : null;

        internal static Func<double> MonotonicSeconds()
        {
            var watch = Stopwatch.StartNew();
            return () => watch.Elapsed.TotalSeconds;
        }

        private sealed class Outgoing
        {
            public Outgoing(string attemptId, string participationId, int epoch)
            {
                AttemptId = attemptId;
                ParticipationId = participationId;
                Epoch = epoch;
            }

            public string AttemptId { get; }

            public string ParticipationId { get; }

            public int Epoch { get; }

            public int LastSeq { get; set; }
        }

        private sealed class Incoming
        {
            private readonly Dictionary<int, string> parts = new Dictionary<int, string>();
            private string? assembling;
            private int expected;

            public Incoming(string attemptId, string participationId, int epoch)
            {
                AttemptId = attemptId;
                ParticipationId = participationId;
                Epoch = epoch;
            }

            public string AttemptId { get; }

            public string ParticipationId { get; }

            public int Epoch { get; }

            public int NextSeq { get; set; } = 1;

            public Dictionary<int, string> Held { get; } = new Dictionary<int, string>();

            /// <summary>
            /// The signal a message completes: a candidate at once, a description
            /// once all its parts are in, assembled by <c>part</c>. Null while parts are missing.
            /// </summary>
            public PeerSignal? Take(Envelope piece)
            {
                if (piece.Candidate != null) return PeerSignal.ForCandidate(piece.Candidate);
                if (assembling != piece.Description || expected != piece.Parts)
                {
                    parts.Clear();
                    assembling = piece.Description;
                    expected = piece.Parts;
                }
                parts[piece.Part] = piece.Sdp;
                if (parts.Count < expected) return null;
                var sdp = new System.Text.StringBuilder();
                for (var part = 1; part <= expected; part++) sdp.Append(parts[part]);
                parts.Clear();
                assembling = null;
                return piece.Description == "offer" ? PeerSignal.Offer(sdp.ToString()) : PeerSignal.Answer(sdp.ToString());
            }
        }

        private sealed class Envelope
        {
            public Envelope(string participationId, int epoch)
            {
                ParticipationId = participationId;
                Epoch = epoch;
            }

            public string ParticipationId { get; }

            public int Epoch { get; }

            /// <summary><c>offer</c> or <c>answer</c>; null for a candidate.</summary>
            public string? Description { get; set; }

            public int Part { get; set; }

            public int Parts { get; set; }

            public string Sdp { get; set; } = "";

            public PeerIceCandidate? Candidate { get; set; }
        }

        private sealed class Unacknowledged
        {
            public Unacknowledged(string toUserId, string attemptId, int seq, string data)
            {
                ToUserId = toUserId;
                AttemptId = attemptId;
                Seq = seq;
                Data = data;
            }

            public string ToUserId { get; }

            public string AttemptId { get; }

            public int Seq { get; }

            public string Data { get; }

            public double NextTry { get; set; }

            public int Retries { get; set; }
        }
    }
}
