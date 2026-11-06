using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Haiven.Sdk
{
    /// <summary>
    /// <see cref="IHaivenSocket"/> over <see cref="ClientWebSocket"/>.
    ///
    /// The desktop and mobile half of the pair whose other half is
    /// <c>WebGlSocket</c>. Unity's IL2CPP desktop and mobile players have a
    /// socket stack and threads; the WebGL player has neither, which is the whole
    /// reason <see cref="IHaivenSocket"/> exists as an interface rather than a
    /// class.
    ///
    /// Two things here are forced by the shape of <see cref="ClientWebSocket"/>
    /// rather than chosen, and both are worth stating because a reader coming
    /// from the TypeScript or Godot binding will expect otherwise:
    ///
    /// <list type="bullet">
    /// <item>
    /// Receiving is a <b>loop on a background task</b>, not a callback. There is
    /// no event to subscribe to — <c>ReceiveAsync</c> is a call that blocks until
    /// a frame arrives — so events are raised from a thread that is not the
    /// caller's. A Unity consumer must marshal to the main thread before touching
    /// anything in the scene; this class deliberately does not do that for them,
    /// because the way to do it differs per host and guessing wrong is worse than
    /// not guessing. The host chooses instead: <see cref="SynchronizationContextSocket"/>
    /// re-raises the events on a captured context, and the Unity package's
    /// <c>UnitySocket</c> does that by default.
    /// </item>
    /// <item>
    /// A frame can arrive <b>in several pieces</b>. <c>EndOfMessage</c> is false
    /// until the last one, and a reader that ignores it silently truncates every
    /// message larger than its buffer. Nothing in this protocol is large today,
    /// which is exactly why that bug would not appear until it did.
    /// </item>
    /// </list>
    /// </summary>
    public sealed class SystemNetSocket : IHaivenSocket, IDisposable
    {
        private readonly ClientWebSocket socket = new ClientWebSocket();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private bool disposed;

        public event Action<string>? Received;

        /// <summary>
        /// Raised when a <see cref="Received"/> subscriber rejects a complete frame.
        /// The receive loop keeps running so one malformed frame or callback bug
        /// cannot hide every later server acknowledgement.
        /// </summary>
        public event Action<string, Exception>? ReceiveFaulted;

        public event Action<int, string>? Closed;

        /// <summary>Connects, then starts the receive loop. Await before sending.</summary>
        public async Task ConnectAsync(Uri url)
        {
            await socket.ConnectAsync(url, cancellation.Token).ConfigureAwait(false);

            // Deliberately not awaited: this runs for the life of the socket.
            _ = Task.Run(ReceiveLoopAsync);
        }

        public void Send(string payload)
        {
            if (disposed || socket.State != WebSocketState.Open)
            {
                // Not an error. The session queues frames until the handshake
                // completes, so "not open yet" is an ordinary state and throwing
                // here would turn a startup race into a crash.
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(payload);

            // Fire-and-forget with the exception observed. An unobserved faulted
            // task is a process-level event in .NET, and a send racing a close is
            // routine rather than exceptional.
            _ = socket
                .SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cancellation.Token)
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Close()
        {
            Close(1000, string.Empty);
        }

        public void Close(int code, string reason)
        {
            if (disposed || socket.State != WebSocketState.Open) return;

            _ = socket
                .CloseAsync((WebSocketCloseStatus)code, reason, cancellation.Token)
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public void Dispose()
        {
            if (disposed) return;

            disposed = true;
            cancellation.Cancel();
            socket.Dispose();
            cancellation.Dispose();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8192];
            var message = new StringBuilder();

            try
            {
                while (socket.State == WebSocketState.Open && !cancellation.IsCancellationRequested)
                {
                    var result = await socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), cancellation.Token)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Closed?.Invoke((int?)result.CloseStatus ?? 1006, result.CloseStatusDescription ?? string.Empty);
                        return;
                    }

                    message.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                    // The continuation case. Without this the first 8 KB of a
                    // large frame would be delivered as if it were the whole
                    // thing, and it would parse as invalid JSON rather than as
                    // anything that names the cause.
                    if (!result.EndOfMessage) continue;

                    var complete = message.ToString();
                    message.Clear();
                    DispatchReceived(complete);
                }
            }
            catch (OperationCanceledException)
            {
                // Ordinary teardown.
            }
            catch (WebSocketException)
            {
                // The peer went away without a close frame. 1006 is what a
                // browser reports for the same thing, so bindings agree.
                Closed?.Invoke(1006, string.Empty);
            }
        }

        internal void DispatchReceived(string payload)
        {
            var handlers = Received;
            if (handlers is null) return;

            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(payload);
                }
                catch (Exception error)
                {
                    ReportReceiveFault(payload, error);
                }
            }
        }

        private void ReportReceiveFault(string payload, Exception error)
        {
            var handlers = ReceiveFaulted;
            if (handlers is null) return;

            foreach (Action<string, Exception> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(payload, error);
                }
                catch (Exception)
                {
                    // Diagnostics must not become a second way to stop receiving.
                }
            }
        }
    }
}
