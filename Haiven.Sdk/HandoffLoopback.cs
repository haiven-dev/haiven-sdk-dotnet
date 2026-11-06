using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Haiven.Sdk
{
    /// <summary>
    /// Receives a game login handoff callback on a loopback HTTP redirect, for
    /// desktop hosts with no custom-scheme or app-link registration (RFC 8252
    /// section 7.3). The redirect is an exact identifier on the Haiven side, so
    /// the port is fixed by the registered redirect URI rather than chosen at
    /// random, and the host must be an IP literal (127.0.0.1 or [::1]), never
    /// <c>localhost</c>, which a browser may resolve to the other address family.
    /// </summary>
    /// <remarks>
    /// The listener answers one matching <c>GET</c> with a static page that
    /// echoes nothing from the request, then stops. Requests for any other path
    /// (a browser's favicon probe, say) get a 404 and do not end the wait. The
    /// complete callback URI is handed back unparsed, so
    /// <see cref="HandoffCoordinator.CompleteAsync"/> performs the exact redirect
    /// and state checks. It never logs the request. Not available on WebGL,
    /// which has no sockets; use an HTTPS callback there.
    /// </remarks>
    public sealed class LoopbackHandoffListener : IDisposable
    {
        private const int MaxRequestBytes = 8192;
        private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(10);

        private const string CompletedPage =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>Haiven</title></head>"
            + "<body style=\"font-family:sans-serif;margin:3em\"><h1>Haiven</h1>"
            + "<p>Sign-in finished. You can close this tab and return to the game.</p></body></html>";

        private readonly TcpListener listener;
        private int disposed;

        private LoopbackHandoffListener(Uri redirectUri, TcpListener listener)
        {
            RedirectUri = redirectUri;
            this.listener = listener;
        }

        /// <summary>The registered redirect this listener serves.</summary>
        public Uri RedirectUri { get; }

        /// <summary>
        /// Binds the redirect's loopback address and port. Bind before opening the
        /// browser, so a port already in use fails the login instead of stranding it.
        /// </summary>
        /// <exception cref="ArgumentException">The redirect is not a loopback HTTP URI with an explicit port.</exception>
        /// <exception cref="HandoffException">The port could not be bound.</exception>
        public static LoopbackHandoffListener Start(Uri redirectUri)
        {
            var address = LoopbackAddress(redirectUri);
            var listener = new TcpListener(address, redirectUri.Port);
            try
            {
                listener.Start();
            }
            catch (SocketException error)
            {
                throw new HandoffException(
                    "The sign-in callback port " + redirectUri.Port + " could not be opened; is another copy of the game running?",
                    error);
            }
            return new LoopbackHandoffListener(redirectUri, listener);
        }

        /// <summary>
        /// Validates that <paramref name="redirectUri"/> can be served by this listener
        /// and returns the address it binds.
        /// </summary>
        public static IPAddress LoopbackAddress(Uri redirectUri)
        {
            if (redirectUri is null) throw new ArgumentNullException(nameof(redirectUri));
            if (!redirectUri.IsAbsoluteUri || redirectUri.Scheme != Uri.UriSchemeHttp)
                throw new ArgumentException("A loopback redirect must be an absolute http URI.", nameof(redirectUri));
            if (redirectUri.IsDefaultPort || redirectUri.Port <= 0)
                throw new ArgumentException("A loopback redirect must name an explicit, registered port.", nameof(redirectUri));
            if (!string.IsNullOrEmpty(redirectUri.Fragment))
                throw new ArgumentException("A loopback redirect must not contain a fragment.", nameof(redirectUri));
            if (redirectUri.HostNameType != UriHostNameType.IPv4 && redirectUri.HostNameType != UriHostNameType.IPv6)
                throw new ArgumentException("A loopback redirect must use 127.0.0.1 or [::1], not a host name.", nameof(redirectUri));
            var address = IPAddress.Parse(redirectUri.IdnHost);
            if (!IPAddress.IsLoopback(address))
                throw new ArgumentException("A loopback redirect must use a loopback address.", nameof(redirectUri));
            return address;
        }

        /// <summary>
        /// Waits for the browser to arrive at the redirect path and returns the full
        /// callback URI it requested. Cancelling stops the listener.
        /// </summary>
        public async Task<Uri> ReceiveAsync(CancellationToken cancellationToken = default)
        {
            using (cancellationToken.Register(Stop))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is ObjectDisposedException || error is SocketException || error is InvalidOperationException)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new HandoffException("The sign-in callback listener stopped.", error);
                    }

                    using (client)
                    {
                        var callback = await ServeAsync(client, cancellationToken).ConfigureAwait(false);
                        if (callback is null) continue;
                        Stop();
                        return callback;
                    }
                }
            }
        }

        public void Dispose() => Stop();

        private void Stop()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            try { listener.Stop(); }
            catch (SocketException) { }
        }

        private async Task<Uri?> ServeAsync(TcpClient client, CancellationToken cancellationToken)
        {
            var stream = client.GetStream();
            string? target;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                timeout.CancelAfter(RequestReadTimeout);
                try
                {
                    target = await ReadTargetAsync(stream, timeout.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException || error is OperationCanceledException || error is ObjectDisposedException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return null;
                }
            }

            if (target is null)
            {
                await RespondAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", "Bad request").ConfigureAwait(false);
                return null;
            }

            var query = target.IndexOf('?');
            var path = query < 0 ? target : target.Substring(0, query);
            if (!string.Equals(path, RedirectUri.AbsolutePath, StringComparison.Ordinal))
            {
                await RespondAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found").ConfigureAwait(false);
                return null;
            }

            if (!Uri.TryCreate(RedirectUri.GetLeftPart(UriPartial.Authority) + target, UriKind.Absolute, out var callback))
            {
                await RespondAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", "Bad request").ConfigureAwait(false);
                return null;
            }

            await RespondAsync(stream, "200 OK", "text/html; charset=utf-8", CompletedPage).ConfigureAwait(false);
            return callback;
        }

        /// <summary>The request target of a <c>GET</c>, or null for anything else.</summary>
        private static async Task<string?> ReadTargetAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[MaxRequestBytes];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer, length, buffer.Length - length, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                length += read;
                if (HeadersEnd(buffer, length) >= 0) break;
            }

            var text = Encoding.ASCII.GetString(buffer, 0, length);
            var lineEnd = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (lineEnd < 0) return null;
            var parts = text.Substring(0, lineEnd).Split(' ');
            if (parts.Length != 3 || parts[0] != "GET" || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
                return null;
            return parts[1].StartsWith("/", StringComparison.Ordinal) ? parts[1] : null;
        }

        private static int HeadersEnd(byte[] buffer, int length)
        {
            for (var i = 3; i < length; i++)
                if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n')
                    return i;
            return -1;
        }

        private static async Task RespondAsync(Stream stream, string status, string contentType, string body)
        {
            var content = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\n"
                + "Content-Type: " + contentType + "\r\n"
                + "Content-Length: " + content.Length + "\r\n"
                + "Cache-Control: no-store\r\n"
                + "Referrer-Policy: no-referrer\r\n"
                + "Connection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
                await stream.WriteAsync(content, 0, content.Length).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
                // The browser went away; the callback, if any, is still complete.
            }
        }
    }

    public static class LoopbackHandoff
    {
        /// <summary>
        /// Runs one login over a loopback redirect: opens the browser through the
        /// coordinator, waits for the callback on <paramref name="listener"/>, and
        /// completes the exchange. The listener must already be started on the
        /// coordinator's redirect URI. Continuations run on the caller's context,
        /// so an engine transport that needs its main thread keeps it.
        /// </summary>
        public static async Task<HandoffCredential> LoginAsync(
            this HandoffCoordinator coordinator,
            LoopbackHandoffListener listener,
            CancellationToken cancellationToken = default)
        {
            if (coordinator is null) throw new ArgumentNullException(nameof(coordinator));
            if (listener is null) throw new ArgumentNullException(nameof(listener));
            coordinator.BeginLogin();
            var callback = await listener.ReceiveAsync(cancellationToken);
            return await coordinator.CompleteAsync(callback, cancellationToken);
        }
    }
}
