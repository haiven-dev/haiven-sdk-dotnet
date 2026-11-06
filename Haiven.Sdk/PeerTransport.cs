using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Haiven.Sdk
{
    /// <summary>The two data channels of one host-guest connection (ADR 0036 point 17).</summary>
    public enum PeerChannel
    {
        /// <summary>Ordered and reliable, label <c>haiven-reliable</c>, negotiated id 0.</summary>
        Reliable = 0,

        /// <summary>Unordered, <c>maxRetransmits: 0</c>, label <c>haiven-unreliable</c>, negotiated id 1.</summary>
        Unreliable = 1,
    }

    /// <summary>Which ICE candidates a connection may use. <c>relay-only</c> sessions use <see cref="Relay"/>.</summary>
    public enum PeerIcePolicy
    {
        All,
        Relay,
    }

    /// <summary>One STUN or TURN server, as <c>p2p.ice.servers</c> returns it.</summary>
    public sealed class PeerIceServer
    {
        public PeerIceServer(IReadOnlyList<string> urls, string? username = null, string? credential = null)
        {
            Urls = urls;
            Username = username;
            Credential = credential;
        }

        public IReadOnlyList<string> Urls { get; }

        public string? Username { get; }

        /// <summary>A TURN password. Never logged (ADR 0036, logging and secrets).</summary>
        public string? Credential { get; }
    }

    /// <summary>An ICE candidate as it travels through session signaling.</summary>
    public sealed class PeerIceCandidate
    {
        public PeerIceCandidate(string candidate, string? sdpMid, int? sdpMLineIndex)
        {
            Candidate = candidate;
            SdpMid = sdpMid;
            SdpMLineIndex = sdpMLineIndex;
        }

        public string Candidate { get; }

        public string? SdpMid { get; }

        public int? SdpMLineIndex { get; }
    }

    /// <summary>Settings a connection is created with. They never change on an open connection (SDK-P2P-12).</summary>
    public sealed class PeerConnectionOptions
    {
        public PeerConnectionOptions(IReadOnlyList<PeerIceServer> iceServers, PeerIcePolicy policy)
        {
            IceServers = iceServers;
            Policy = policy;
        }

        public IReadOnlyList<PeerIceServer> IceServers { get; }

        public PeerIcePolicy Policy { get; }
    }

    /// <summary>
    /// The seam between the SDK and a WebRTC library: one peer connection with
    /// the two negotiated data channels already created.
    ///
    /// The library is behind an interface because no single one covers every
    /// target, and because the controller and the conformance runner must run
    /// without any WebRTC at all. The DTLS fingerprint check of SDK-P2P-13 is the
    /// library's: it refuses a handshake whose certificate does not match the
    /// fingerprint in the remote description, so the description a connection is
    /// given must be the one that arrived through this session's signaling.
    /// </summary>
    public interface IHaivenPeerTransport : IDisposable
    {
        /// <summary>Guest side. The local offer SDP, already set as the local description.</summary>
        Task<string> CreateOfferAsync();

        /// <summary>Host side. Applies the remote offer and returns the local answer SDP.</summary>
        Task<string> AcceptOfferAsync(string offerSdp);

        /// <summary>Guest side. Applies the host's answer.</summary>
        Task AcceptAnswerAsync(string answerSdp);

        /// <summary>Hands a remote candidate to ICE. Call only after the remote description is applied.</summary>
        void AddRemoteCandidate(PeerIceCandidate candidate);

        /// <summary>Sends one message. False when the library refused it.</summary>
        bool Send(PeerChannel channel, byte[] payload);

        /// <summary>Bytes the library has accepted on this channel and not yet handed to the network.</summary>
        long BufferedAmount(PeerChannel channel);

        /// <summary>The last measured round-trip time on the selected path, in milliseconds; -1 before the first measurement.</summary>
        long RoundTripTimeMs { get; }

        /// <summary>
        /// The ICE type of the local candidate on the selected path: <c>host</c>,
        /// <c>srflx</c>, <c>prflx</c> or <c>relay</c>. Null before the first measurement.
        /// </summary>
        string? PathType { get; }

        /// <summary>Starts a new measurement of <see cref="RoundTripTimeMs"/> and <see cref="PathType"/> unless one is running.</summary>
        void RefreshRoundTripTime();

        /// <summary>Raised for each local candidate, to be sent through signaling.</summary>
        event Action<PeerIceCandidate>? LocalCandidate;

        /// <summary>Raised once per channel when it opens.</summary>
        event Action<PeerChannel>? ChannelOpened;

        /// <summary>Raised for each message received.</summary>
        event Action<PeerChannel, byte[]>? Received;

        /// <summary>Raised once when the connection fails or closes, with a reason for logs.</summary>
        event Action<string>? Closed;
    }

    /// <summary>What <see cref="HaivenPeerLink.Send"/> did with a message.</summary>
    public enum PeerSendResult
    {
        Sent,

        /// <summary>Both channels are not open yet, or the link is closed.</summary>
        NotOpen,

        /// <summary>Larger than the channel's limit. Nothing was sent.</summary>
        TooLarge,

        /// <summary>An unreliable message did not fit in the send queue and was dropped.</summary>
        Dropped,

        /// <summary>A reliable message did not fit in the send queue (<c>send_queue_full</c>). Nothing was sent.</summary>
        QueueFull,
    }

    /// <summary>
    /// One host-guest connection with the limits of ADR 0036 point 17 applied
    /// over any <see cref="IHaivenPeerTransport"/>.
    ///
    /// The limits live here and not in each library adapter, so every library
    /// refuses the same messages. The send queue is the library's own buffer,
    /// read through <see cref="IHaivenPeerTransport.BufferedAmount"/>: libwebrtc
    /// closes a data channel whose buffer passes 16 MiB, so the cap has to be
    /// checked before a send rather than learned from a failure.
    /// </summary>
    public sealed class HaivenPeerLink : IDisposable
    {
        public const int MaxUnreliableMessage = 1200;
        public const int MaxReliableMessage = 64 * 1024;
        public const long MaxSendQueue = 1024 * 1024;

        private readonly IHaivenPeerTransport transport;
        private readonly HashSet<PeerChannel> open = new HashSet<PeerChannel>();
        private bool closed;

        public HaivenPeerLink(IHaivenPeerTransport transport)
        {
            this.transport = transport;
            transport.ChannelOpened += OnChannelOpened;
            transport.Received += OnReceived;
            transport.Closed += OnClosed;
        }

        /// <summary>The library connection, for the offer, answer and candidate exchange.</summary>
        public IHaivenPeerTransport Transport => transport;

        public bool IsOpen => !closed && open.Count == 2;

        /// <summary>Raised once, when both channels are open.</summary>
        public event Action? Opened;

        public event Action<byte[], bool>? Message;

        /// <summary>Raised once, when the connection fails, closes or is disposed.</summary>
        public event Action<string>? Closed;

        public PeerSendResult Send(byte[] payload, bool reliable)
        {
            if (!IsOpen) return PeerSendResult.NotOpen;
            var limit = reliable ? MaxReliableMessage : MaxUnreliableMessage;
            if (payload.Length > limit) return PeerSendResult.TooLarge;

            var queued = transport.BufferedAmount(PeerChannel.Reliable)
                + transport.BufferedAmount(PeerChannel.Unreliable);
            if (queued + payload.Length > MaxSendQueue)
                return reliable ? PeerSendResult.QueueFull : PeerSendResult.Dropped;

            var channel = reliable ? PeerChannel.Reliable : PeerChannel.Unreliable;
            if (transport.Send(channel, payload)) return PeerSendResult.Sent;
            // The library refuses a send only on a channel that is closing, and
            // the Closed event follows. Report it as the queue would, so a
            // reliable caller treats the peer as gone.
            return reliable ? PeerSendResult.QueueFull : PeerSendResult.Dropped;
        }

        public void Dispose() => Close("disposed");

        private void OnChannelOpened(PeerChannel channel)
        {
            if (closed || !open.Add(channel)) return;
            if (open.Count == 2) Opened?.Invoke();
        }

        private void OnReceived(PeerChannel channel, byte[] payload)
        {
            // A channel can deliver before the other one opens; that message
            // still belongs to the peer.
            if (closed) return;
            Message?.Invoke(payload, channel == PeerChannel.Reliable);
        }

        private void OnClosed(string reason) => Close(reason);

        private void Close(string reason)
        {
            if (closed) return;
            closed = true;
            transport.ChannelOpened -= OnChannelOpened;
            transport.Received -= OnReceived;
            transport.Closed -= OnClosed;
            transport.Dispose();
            Closed?.Invoke(reason);
        }
    }
}
