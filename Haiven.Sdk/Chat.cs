using System;
using System.Collections.Generic;
using System.Linq;
using Haiven.Sdk.Contracts;

namespace Haiven.Sdk
{
    /// <summary>Where a message this player sent stands.</summary>
    public enum OutgoingChatState
    {
        /// <summary>Written, or queued until the session authenticates; no reply yet.</summary>
        Sending,
        /// <summary>The socket closed before the reply. Attaching the next session resends it.</summary>
        Unconfirmed,
        /// <summary>Accepted and visible to the channel (<c>allowed</c> or <c>warned</c>).</summary>
        Accepted,
        /// <summary>Accepted but held for moderation (<c>pending</c> or <c>review_pending</c>).</summary>
        Held,
        /// <summary>Accepted but not shown (<c>hidden</c> or <c>blocked</c>).</summary>
        Removed,
        /// <summary>Refused with an error code; resending the same message cannot succeed.</summary>
        Refused,
    }

    /// <summary>One message this player sent, tracked by its client message id.</summary>
    public sealed class OutgoingChatMessage
    {
        internal OutgoingChatMessage(string clientMessageId, string channelId, string content, string? parentMessageId)
        {
            ClientMessageId = clientMessageId;
            ChannelId = channelId;
            Content = content;
            ParentMessageId = parentMessageId;
        }

        /// <summary>The deduplication key: a resend under it is the same message to the server.</summary>
        public string ClientMessageId { get; }
        public string ChannelId { get; }
        public string Content { get; }
        public string? ParentMessageId { get; }
        public OutgoingChatState State { get; internal set; } = OutgoingChatState.Sending;
        /// <summary>The server's message id, once a reply named it.</summary>
        public string? MessageId { get; internal set; }
        /// <summary>The server's moderation state, as the reply carried it.</summary>
        public string? ModerationState { get; internal set; }
        /// <summary>The error code of a refusal.</summary>
        public string? Error { get; internal set; }
    }

    /// <summary>
    /// Headless chat over the session core: send, receive and typing, with no UI.
    ///
    /// A session is one socket, so this sits above sessions. Each send carries a
    /// client message id. When a socket closes before a send's reply, the send
    /// becomes <see cref="OutgoingChatState.Unconfirmed"/>, and <see cref="Attach"/>
    /// on the next session resends it under the same id. The server treats that
    /// as the same message (haiven-api chat clientMessageId deduplication), so a
    /// lost reply never becomes a lost or doubled message.
    ///
    /// Nothing here throws on a network failure: a send without a usable session
    /// is kept and resent on attach. That is what keeps a chat failure from
    /// interrupting gameplay.
    /// </summary>
    public sealed class HaivenChat : IDisposable
    {
        private readonly Func<string> newClientMessageId;
        private readonly List<OutgoingChatMessage> outbox = new List<OutgoingChatMessage>();
        // The request each in-flight send went out under, on the attached session.
        private readonly Dictionary<string, OutgoingChatMessage> byRequestId = new Dictionary<string, OutgoingChatMessage>();
        private HaivenSessionCore? session;

        public HaivenChat(Func<string>? newClientMessageId = null)
        {
            this.newClientMessageId = newClientMessageId ?? (() => Guid.NewGuid().ToString());
        }

        /// <summary>Messages this player sent, oldest first, until <see cref="Forget"/>.</summary>
        public IReadOnlyList<OutgoingChatMessage> Outbox => outbox;

        /// <summary>Raised whenever an outgoing message changes state.</summary>
        public event Action<OutgoingChatMessage>? OutgoingChanged;

        /// <summary><c>chat.message</c>: a message reached a channel this player is in.</summary>
        public event Action<ChatMessageV0>? MessageReceived;

        /// <summary><c>chat.message.updated</c>: a message was edited.</summary>
        public event Action<ChatMessageV0>? MessageUpdated;

        /// <summary><c>chat.message.deleted</c>: a message was deleted.</summary>
        public event Action<ChatMessageV0>? MessageDeleted;

        /// <summary><c>chat.typing</c>: someone is typing.</summary>
        public event Action<ChatTypingV0>? Typing;

        /// <summary>
        /// Uses this session from now on, and resends every unconfirmed message
        /// through it. Attach each new session after a reconnect.
        /// </summary>
        public void Attach(HaivenSessionCore next)
        {
            if (next is null) throw new ArgumentNullException(nameof(next));
            Detach();
            session = next;
            next.ReplyReceived += OnReply;
            next.EventReceived += OnEvent;
            next.Closed += OnClosed;
            foreach (var message in outbox.Where(m => m.State == OutgoingChatState.Unconfirmed).ToList())
            {
                Write(message);
            }
        }

        /// <summary>Sends a message, or keeps it to send on the next attach.</summary>
        public OutgoingChatMessage Send(string channelId, string content, string? parentMessageId = null)
        {
            if (string.IsNullOrEmpty(channelId)) throw new ArgumentException("haiven: channelId is required", nameof(channelId));
            if (content is null) throw new ArgumentNullException(nameof(content));
            var message = new OutgoingChatMessage(newClientMessageId(), channelId, content, parentMessageId);
            outbox.Add(message);
            if (session is { State: not HaivenSessionState.Closed }) Write(message);
            else Change(message, OutgoingChatState.Unconfirmed);
            return message;
        }

        /// <summary>Sends <c>chat.typing</c>. Typing is best effort and is never resent.</summary>
        public void SendTyping(string channelId)
        {
            if (session is { State: not HaivenSessionState.Closed })
            {
                session.Send(WsTypes.ChatTyping, new ChatTypingV0 { ChannelId = channelId });
            }
        }

        /// <summary>Stops tracking messages that reached a final state.</summary>
        public void Forget(OutgoingChatMessage message) => outbox.Remove(message);

        public void Dispose() => Detach();

        private void Write(OutgoingChatMessage message)
        {
            var frame = session!.Send(WsTypes.ChatSend, new ChatSendRequestV0
            {
                ChannelId = message.ChannelId,
                ParentMessageId = message.ParentMessageId,
                ClientMessageId = message.ClientMessageId,
                Content = message.Content,
            });
            byRequestId[frame.RequestId] = message;
            Change(message, OutgoingChatState.Sending);
        }

        private void Detach()
        {
            if (session is null) return;
            session.ReplyReceived -= OnReply;
            session.EventReceived -= OnEvent;
            session.Closed -= OnClosed;
            session = null;
            byRequestId.Clear();
        }

        private void OnReply(ReceivedFrame received)
        {
            var frame = received.Frame;
            if (frame.RequestId is null || !byRequestId.TryGetValue(frame.RequestId, out var message)) return;
            byRequestId.Remove(frame.RequestId);
            if (frame.Success != true)
            {
                message.Error = frame.Error;
                Change(message, OutgoingChatState.Refused);
                return;
            }
            var submission = WsApi.DecodeChatSendReply(frame);
            message.MessageId = submission?.Message?.Id;
            message.ModerationState = submission?.ModerationState;
            Change(message, StateFor(submission?.ModerationState));
        }

        private void OnEvent(ReceivedFrame received)
        {
            var frame = received.Frame;
            switch (frame.Type)
            {
                case WsTypes.ChatMessage when WsApi.Decode<ChatMessageV0>(frame) is { } message:
                    MessageReceived?.Invoke(message);
                    break;
                case WsTypes.ChatMessageUpdated when WsApi.Decode<ChatMessageV0>(frame) is { } message:
                    MessageUpdated?.Invoke(message);
                    break;
                case WsTypes.ChatMessageDeleted when WsApi.Decode<ChatMessageV0>(frame) is { } message:
                    MessageDeleted?.Invoke(message);
                    break;
                case WsTypes.ChatTyping when WsApi.Decode<ChatTypingV0>(frame) is { } typing:
                    Typing?.Invoke(typing);
                    break;
            }
        }

        private void OnClosed(int code, string reason)
        {
            // Unanswered sends may or may not have landed; the next attach resends
            // them under the same client message id, which the server deduplicates.
            foreach (var message in byRequestId.Values.ToList()) Change(message, OutgoingChatState.Unconfirmed);
            byRequestId.Clear();
        }

        private void Change(OutgoingChatMessage message, OutgoingChatState state)
        {
            message.State = state;
            OutgoingChanged?.Invoke(message);
        }

        internal static OutgoingChatState StateFor(string? moderationState) => moderationState switch
        {
            "allowed" or "warned" => OutgoingChatState.Accepted,
            "hidden" or "blocked" => OutgoingChatState.Removed,
            // pending, review_pending, and any state minted after this build:
            // the message exists but is not known to be visible.
            _ => OutgoingChatState.Held,
        };
    }
}
