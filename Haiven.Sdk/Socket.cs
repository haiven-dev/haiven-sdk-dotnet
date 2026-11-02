namespace Haiven.Sdk
{
    /// <summary>
    /// The minimum this SDK needs from a WebSocket.
    ///
    /// Injected for the same reason the TypeScript core injects one, but the
    /// reason bites harder here. Unity's supported targets do not agree on
    /// whether sockets exist: a desktop or mobile IL2CPP build can use
    /// System.Net.WebSockets, and a WebGL build cannot — it has no threads and no
    /// socket stack, so the transport has to be a .jslib bridge into the
    /// browser's own WebSocket. A binding that named a concrete class would
    /// compile everywhere and fail on the one target games actually ship to most.
    /// </summary>
    public interface IHaivenSocket
    {
        void Send(string payload);

        void Close();

        /// <summary>Raised for each frame, on whichever thread the transport uses.</summary>
        event System.Action<string>? Received;

        /// <summary>Raised once, with the close code the server sent.</summary>
        event System.Action<int, string>? Closed;
    }
}
