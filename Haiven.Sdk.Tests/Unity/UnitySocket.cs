using System;
using System.Threading;
using System.Threading.Tasks;

#nullable enable

namespace Haiven.Sdk
{
    /// <summary>
    /// The Unity package's realtime socket for the editor and for desktop and
    /// mobile players: <see cref="SystemNetSocket"/> with its events delivered
    /// on Unity's main thread.
    ///
    /// <see cref="SystemNetSocket"/> raises <c>Received</c> and <c>Closed</c>
    /// from its receive loop, a thread-pool thread. A chat callback that touches
    /// the scene from there throws, or worse, corrupts state without throwing.
    /// Unreal marshals every callback to the game thread and
    /// <see cref="UnityHandoffDispatcher"/> does the same for login; this does it
    /// for realtime, by default, so the safe behaviour is the one a game gets
    /// without asking.
    ///
    /// Construct it on the main thread (in <c>Awake</c>, <c>Start</c> or a
    /// coroutine), where <see cref="SynchronizationContext.Current"/> is Unity's.
    /// Constructed anywhere else it throws rather than guess: a socket that
    /// silently delivered on the receive loop would be the bug this exists to
    /// prevent. Events are posted, so they arrive in order on a later frame.
    ///
    /// Pass <see cref="HaivenDispatch.Direct"/> to opt out and receive on the
    /// receive loop, for a host that marshals for itself or runs no scene work
    /// in its handlers. WebGL players use <c>WebGlSocket</c> instead, whose
    /// callbacks already arrive on the only thread there is.
    /// </summary>
    public sealed class UnitySocket : IHaivenSocket, IDisposable
    {
        private readonly SystemNetSocket transport = new SystemNetSocket();
        private readonly IHaivenSocket events;

        public UnitySocket(HaivenDispatch dispatch = HaivenDispatch.MainThread)
        {
            if (dispatch == HaivenDispatch.Direct)
            {
                events = transport;
                return;
            }

            if (dispatch != HaivenDispatch.MainThread)
            {
                transport.Dispose();
                throw new ArgumentOutOfRangeException(nameof(dispatch), dispatch, "Unknown HaivenDispatch.");
            }

            var context = SynchronizationContext.Current;
            if (context is null)
            {
                transport.Dispose();
                throw new InvalidOperationException(
                    "Create UnitySocket on Unity's main thread, or pass HaivenDispatch.Direct.");
            }

            events = new SynchronizationContextSocket(transport, context);
        }

        /// <summary>Raised for each frame, on Unity's main thread unless opted out.</summary>
        public event Action<string>? Received
        {
            add => events.Received += value;
            remove => events.Received -= value;
        }

        /// <summary>Raised once, on Unity's main thread unless opted out.</summary>
        public event Action<int, string>? Closed
        {
            add => events.Closed += value;
            remove => events.Closed -= value;
        }

        /// <summary>Connects, then starts the receive loop. Await before sending.</summary>
        public Task ConnectAsync(Uri url) => transport.ConnectAsync(url);

        public void Send(string payload) => transport.Send(payload);

        public void Close() => transport.Close();

        public void Close(int code, string reason) => transport.Close(code, reason);

        public void Dispose() => (events as IDisposable)?.Dispose();
    }
}
