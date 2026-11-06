using System;
using System.Threading;

namespace Haiven.Sdk
{
    /// <summary>
    /// An <see cref="IHaivenSocket"/> that raises its inner socket's events on a
    /// <see cref="SynchronizationContext"/>.
    ///
    /// This is the <c>dispatch.sync-context</c> adapter from the engine-quirks
    /// plan, written once by hand until the effect interpreter is generated. It
    /// exists because <see cref="SystemNetSocket"/> raises every event from its
    /// receive loop, and a host whose scene API is main-thread-only (Unity) has
    /// to be handed events on that thread. Putting the marshalling in a
    /// decorator keeps the transport host-neutral, which is the reason
    /// <see cref="SystemNetSocket"/> gives for not doing it itself.
    ///
    /// Every event is <b>posted</b>, even when it is raised on the context
    /// already. Posting is FIFO on the contexts that matter (Unity's drains a
    /// queue once per frame), so a frame and the close that follows it keep their
    /// order; invoking inline when "already there" would let a close overtake
    /// frames still queued. Events still queued when the socket is disposed are
    /// dropped: a disposed socket has no listener that expects them.
    /// </summary>
    public sealed class SynchronizationContextSocket : IHaivenSocket, IDisposable
    {
        private readonly IHaivenSocket inner;
        private readonly SynchronizationContext context;
        private volatile bool disposed;

        public SynchronizationContextSocket(IHaivenSocket inner, SynchronizationContext context)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            inner.Received += OnReceived;
            inner.Closed += OnClosed;
        }

        /// <summary>Raised for each frame, on the captured context.</summary>
        public event Action<string>? Received;

        /// <summary>Raised once, on the captured context.</summary>
        public event Action<int, string>? Closed;

        public void Send(string payload) => inner.Send(payload);

        public void Close() => inner.Close();

        public void Dispose()
        {
            if (disposed) return;

            disposed = true;
            inner.Received -= OnReceived;
            inner.Closed -= OnClosed;
            (inner as IDisposable)?.Dispose();
        }

        private void OnReceived(string payload)
        {
            context.Post(_ =>
            {
                if (!disposed) Received?.Invoke(payload);
            }, null);
        }

        private void OnClosed(int code, string reason)
        {
            context.Post(_ =>
            {
                if (!disposed) Closed?.Invoke(code, reason);
            }, null);
        }
    }
}
