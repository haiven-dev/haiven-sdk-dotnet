// Quirk scenario `deliver-on-host-main`,
// run against the Unity package's realtime socket.
//
// `SystemNetSocket` raises every event from its receive loop, which is a
// thread-pool thread. Unity's scene API may only be touched from the main
// thread, so a chat callback that updates the UI from that loop throws or
// corrupts state. The engine-free stand-in for "Unity's main thread" here is a
// single-threaded `SynchronizationContext` pumped by one dedicated thread, which
// is the same shape as `UnitySynchronizationContext`: `Post` enqueues, the main
// loop drains. The transport is real: a loopback WebSocket server, so the
// off-main delivery is the receive loop's, not a fake's.

using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;

static class MainThreadDelivery
{
    public sealed record Outcome(
        int MainThread,
        int ReceivedThread,
        int ClosedThread,
        string? Received,
        int? CloseCode,
        bool Completed);

    /// <summary>
    /// Connects a <see cref="UnitySocket"/> created on the pumped main thread,
    /// receives one frame and one close, and records which thread each event
    /// arrived on.
    /// </summary>
    public static Outcome Run(Func<UnitySocket> create)
    {
        using var server = LoopbackServer.Start();
        using var loop = new MainLoop();
        var receivedThread = -1;
        var closedThread = -1;
        string? received = null;
        int? closeCode = null;
        var done = new ManualResetEventSlim();

        loop.Post(async () =>
        {
            var socket = create();
            socket.Received += payload =>
            {
                receivedThread = Environment.CurrentManagedThreadId;
                received = payload;
            };
            socket.Closed += (code, _) =>
            {
                closedThread = Environment.CurrentManagedThreadId;
                closeCode = code;
                socket.Dispose();
                done.Set();
            };
            await socket.ConnectAsync(server.Url);
        });

        var completed = done.Wait(TimeSpan.FromSeconds(10));
        return new Outcome(loop.ThreadId, receivedThread, closedThread, received, closeCode, completed);
    }

    /// <summary>
    /// Off any synchronization context, the default socket has no main thread to
    /// deliver to. It refuses at construction rather than silently delivering on
    /// the receive loop, which is the bug this exists to prevent.
    /// </summary>
    public static bool DefaultRefusesWithoutAMainContext()
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            using var socket = new UnitySocket();
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>A single-threaded context pumped by one dedicated thread.</summary>
    private sealed class MainLoop : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback, object?)> queue = new();
        private readonly Thread thread;

        public MainLoop()
        {
            thread = new Thread(Pump) { IsBackground = true, Name = "host-main" };
            thread.Start();
        }

        public int ThreadId => thread.ManagedThreadId;

        public void Post(Func<Task> work) => Post(_ => _ = work(), null);

        public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new NotSupportedException("the host main loop only accepts posts");

        public void Dispose()
        {
            queue.CompleteAdding();
            thread.Join(TimeSpan.FromSeconds(5));
        }

        private void Pump()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in queue.GetConsumingEnumerable()) callback(state);
        }
    }

    /// <summary>Sends <c>ready</c>, then closes with 4000.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener listener;

        private LoopbackServer(HttpListener listener, Uri url)
        {
            this.listener = listener;
            Url = url;
        }

        public Uri Url { get; }

        public static LoopbackServer Start()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            var server = new LoopbackServer(listener, new Uri($"ws://127.0.0.1:{port}/socket"));
            _ = Task.Run(server.ServeOneAsync);
            return server;
        }

        public void Dispose() => listener.Close();

        private async Task ServeOneAsync()
        {
            try
            {
                var context = await listener.GetContextAsync().ConfigureAwait(false);
                var accepted = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
                var socket = accepted.WebSocket;
                await socket
                    .SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes("ready")), WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
                await socket
                    .CloseOutputAsync((WebSocketCloseStatus)4000, "done", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception) when (!listener.IsListening)
            {
                // Torn down by Dispose.
            }
        }
    }
}
