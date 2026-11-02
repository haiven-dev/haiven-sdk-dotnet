using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Haiven.Sdk
{
    public enum HaivenSessionState
    {
        Pending,
        Authenticated,
        Expired,
        Closed,
    }

    /// <summary>A parsed server frame together with the exact text received.</summary>
    public sealed class ReceivedFrame
    {
        public ReceivedFrame(string json, ServerFrame frame)
        {
            Json = json;
            Frame = frame;
        }

        public string Json { get; }

        public ServerFrame Frame { get; }
    }

    /// <summary>
    /// Engine-neutral Haiven session state over an injected socket.
    ///
    /// The host owns connection establishment. Call <see cref="Opened"/> once
    /// the socket is writable; the session then performs the required first-frame
    /// authentication, queues application frames until it succeeds, and
    /// re-authenticates on the same socket after <c>auth.expired</c>.
    /// </summary>
    public sealed class HaivenSessionCore : IDisposable
    {
        private const string Auth = "auth";
        private const string AuthSuccess = "auth.success";
        private const string AuthExpired = "auth.expired";

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly IHaivenSocket socket;
        private readonly Func<string> tokenSource;
        private readonly Func<string> nextRequestId;
        private readonly Queue<ClientFrame> pending = new Queue<ClientFrame>();
        private bool opened;
        private bool disposed;

        public HaivenSessionCore(
            IHaivenSocket socket,
            Func<string> tokenSource,
            Func<string>? nextRequestId = null)
        {
            this.socket = socket ?? throw new ArgumentNullException(nameof(socket));
            this.tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
            this.nextRequestId = nextRequestId ?? DefaultRequestId;
            socket.Received += Receive;
            socket.Closed += SocketClosed;
        }

        public HaivenSessionState State { get; private set; } = HaivenSessionState.Pending;

        public event Action<ReceivedFrame>? FrameReceived;

        public event Action<ReceivedFrame>? ReplyReceived;

        public event Action<ReceivedFrame>? EventReceived;

        public event Action? Authenticated;

        public event Action? Expired;

        public event Action<int, string>? Closed;

        /// <summary>Starts the required first-frame authentication exactly once.</summary>
        public void Opened()
        {
            ThrowIfDisposed();
            if (State == HaivenSessionState.Closed)
            {
                throw new InvalidOperationException("haiven: cannot open a closed session");
            }

            if (opened) return;
            opened = true;
            Authenticate();
        }

        /// <summary>Sends a frame, or queues it until authentication succeeds.</summary>
        public ClientFrame Send(string type, object? payload = null)
        {
            ThrowIfDisposed();
            if (State == HaivenSessionState.Closed)
            {
                throw new InvalidOperationException("haiven: send on a closed session");
            }

            var frame = new ClientFrame
            {
                Type = type,
                RequestId = nextRequestId(),
                Payload = payload,
            };

            if (State == HaivenSessionState.Authenticated)
            {
                Write(frame);
            }
            else
            {
                pending.Enqueue(frame);
            }

            return frame;
        }

        public void Close()
        {
            ThrowIfDisposed();
            if (State != HaivenSessionState.Closed) socket.Close();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            socket.Received -= Receive;
            socket.Closed -= SocketClosed;
        }

        private void Authenticate()
        {
            Write(new ClientFrame
            {
                Type = Auth,
                RequestId = nextRequestId(),
                Payload = tokenSource(),
            });
        }

        private void Write(ClientFrame frame)
        {
            socket.Send(JsonSerializer.Serialize(frame, JsonOptions));
        }

        private void Flush()
        {
            while (pending.Count > 0) Write(pending.Dequeue());
        }

        private void Receive(string json)
        {
            var received = new ReceivedFrame(json, ServerFrame.Parse(json));
            FrameReceived?.Invoke(received);

            if (received.Frame.Type == AuthSuccess)
            {
                State = HaivenSessionState.Authenticated;
                Authenticated?.Invoke();
                Flush();
                return;
            }

            if (received.Frame.Type == AuthExpired)
            {
                if (State != HaivenSessionState.Expired)
                {
                    State = HaivenSessionState.Expired;
                    Expired?.Invoke();
                    Authenticate();
                }
                return;
            }

            if (received.Frame.IsReply)
            {
                ReplyReceived?.Invoke(received);
            }
            else
            {
                EventReceived?.Invoke(received);
            }
        }

        private void SocketClosed(int code, string reason)
        {
            if (State == HaivenSessionState.Closed) return;
            State = HaivenSessionState.Closed;
            Closed?.Invoke(code, reason);
        }

        private static string DefaultRequestId() => Guid.NewGuid().ToString("D");

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(HaivenSessionCore));
        }
    }
}
