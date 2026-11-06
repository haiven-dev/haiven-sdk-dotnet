using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Haiven.Sdk.Json;

namespace Haiven.Sdk
{
    /// <summary>
    /// The game socket as P2P sessions use it: requests matched to their
    /// replies, the socket's <c>connectionId</c>, and server events handed to
    /// the game's thread.
    ///
    /// <see cref="HaivenSessionCore"/> raises frames on the socket's thread.
    /// A reply completes its task there, so an <c>await</c> resumes on the
    /// caller's context. Events and the close wait in a queue until
    /// <see cref="Pump"/> runs them, in the order they arrived.
    /// </summary>
    public sealed class HaivenSessionChannel : IDisposable
    {
        private const string AuthSuccess = "auth.success";

        private readonly HaivenSessionCore session;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ServerFrame>> waiting =
            new ConcurrentDictionary<string, TaskCompletionSource<ServerFrame>>();
        private readonly ConcurrentDictionary<string, Action<ServerFrame>> handlers =
            new ConcurrentDictionary<string, Action<ServerFrame>>();
        private readonly ConcurrentQueue<Action> inbox = new ConcurrentQueue<Action>();

        // The session core picks the request id inside Send, so a fast reply can
        // land before its waiter is registered. It is parked here and claimed.
        private readonly ConcurrentDictionary<string, ServerFrame> early = new ConcurrentDictionary<string, ServerFrame>();
        private volatile string? connectionId;
        private HaivenSessionCloseKind? closeKind;
        private string? closeReason;

        /// <param name="session">The game socket's session, opened with the game credential (SDK-P2P-1).</param>
        /// <param name="connectionId">The id from <c>auth.success</c>, when the socket authenticated before this channel existed.</param>
        public HaivenSessionChannel(HaivenSessionCore session, string? connectionId = null)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.connectionId = string.IsNullOrEmpty(connectionId) ? null : connectionId;
            session.FrameReceived += OnFrame;
            session.ReplyReceived += OnReply;
            session.EventReceived += OnEvent;
            session.Closed += OnClosed;
        }

        /// <summary>The server's id for this socket, from <c>auth.success</c>; null until then and after a close.</summary>
        public string? ConnectionId => connectionId;

        /// <summary>Authenticated, with a known <see cref="ConnectionId"/>.</summary>
        public bool IsOpen => session.State == HaivenSessionState.Authenticated && connectionId != null;

        /// <summary>A server event, from <see cref="Pump"/>: its frame.</summary>
        public event Action<ServerFrame>? Event;

        /// <summary>The socket closed, from <see cref="Pump"/>. <see cref="CloseKind"/> says how.</summary>
        public event Action? Lost;

        /// <summary>How the socket closed, classified by <see cref="HaivenSessionClose"/>; null while it is open.</summary>
        public HaivenSessionCloseKind? CloseKind => closeKind;

        /// <summary>The server's close reason, such as <c>credential_revoked</c>; null while the socket is open.</summary>
        public string? CloseReason => closeReason;

        /// <summary>Sends one operation and waits for its reply, refused or not.</summary>
        /// <exception cref="InvalidOperationException">The socket is closed.</exception>
        /// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>.</exception>
        public async Task<ServerFrame> RequestAsync(string type, object? payload, TimeSpan timeout)
        {
            if (session.State == HaivenSessionState.Closed) throw new InvalidOperationException("haiven: the game socket is closed");
            var sent = session.Send(type, payload);
            var reply = Wait(sent.RequestId);
            var finished = await Task.WhenAny(reply.Task, Task.Delay(timeout)).ConfigureAwait(true);
            waiting.TryRemove(sent.RequestId, out _);
            if (finished != reply.Task) throw new TimeoutException("haiven: no reply to " + type);
            return await reply.Task.ConfigureAwait(true);
        }

        /// <summary>
        /// Sends one operation without waiting. Its reply, when one comes, is
        /// handed to <paramref name="onReply"/> from <see cref="Pump"/>. A closed
        /// socket drops the message.
        /// </summary>
        public void Send(string type, object? payload, Action<ServerFrame>? onReply = null)
        {
            if (session.State == HaivenSessionState.Closed) return;
            var sent = session.Send(type, payload);
            // Claimed even when nobody reads it, so its reply is not parked forever.
            var handler = onReply ?? (_ => { });
            handlers[sent.RequestId] = handler;
            if (early.TryRemove(sent.RequestId, out var arrived) && handlers.TryRemove(sent.RequestId, out _))
                inbox.Enqueue(() => handler(arrived));
        }

        /// <summary>Runs queued events and replies on the calling thread.</summary>
        public void Pump()
        {
            while (inbox.TryDequeue(out var action)) action();
        }

        public void Dispose()
        {
            session.FrameReceived -= OnFrame;
            session.ReplyReceived -= OnReply;
            session.EventReceived -= OnEvent;
            session.Closed -= OnClosed;
            foreach (var pending in waiting.Values) pending.TrySetCanceled();
            waiting.Clear();
            handlers.Clear();
        }

        private TaskCompletionSource<ServerFrame> Wait(string requestId)
        {
            var reply = new TaskCompletionSource<ServerFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting[requestId] = reply;
            if (early.TryRemove(requestId, out var arrived) && waiting.TryRemove(requestId, out _))
                reply.TrySetResult(arrived);
            return reply;
        }

        private void OnFrame(ReceivedFrame received)
        {
            var frame = received.Frame;
            if (frame.Type != AuthSuccess || frame.Payload == null) return;
            if (frame.Payload.TryGetProperty("connectionId", out var id) && id.Kind == HaivenJsonKind.String)
            {
                var value = id.GetString();
                if (value.Length > 0) connectionId = value;
            }
        }

        private void OnReply(ReceivedFrame received)
        {
            var id = received.Frame.RequestId;
            if (string.IsNullOrEmpty(id)) return;
            var frame = received.Frame;
            if (waiting.TryRemove(id!, out var reply)) reply.TrySetResult(frame);
            else if (handlers.TryRemove(id!, out var handler)) inbox.Enqueue(() => handler(frame));
            else early[id!] = frame;
        }

        private void OnEvent(ReceivedFrame received)
        {
            var frame = received.Frame;
            inbox.Enqueue(() => Event?.Invoke(frame));
        }

        private void OnClosed(int code, string reason)
        {
            connectionId = null;
            closeReason = reason;
            closeKind = HaivenSessionClose.Classify(code, reason);
            inbox.Enqueue(() => Lost?.Invoke());
        }
    }
}
