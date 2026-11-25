# .NET SDK — the portable core

Normative client behaviour (session lifecycle, recovery, open vocabularies, REST, handoff) is specified in
the Haiven SDK client specification.

## Install

Download `haiven-dotnet-portable.tar.gz` from the
[.NET SDK release](https://github.com/haiven-dev/haiven-sdk-dotnet/releases/tag/v0.1.0-preview.1).
Extract it into your project folder. The release provides
`SHA256SUMS.binaries` for the archive. From your project folder, install
the core package from the extracted NuGet feed.

```sh
dotnet add package Haiven.Sdk --version 0.1.0-preview.1 --source ./haiven-dotnet-portable
```

To build against source, clone the public release tag into your project
folder and add a project reference.

```sh
git clone --depth 1 --branch v0.1.0-preview.1 https://github.com/haiven-dev/haiven-sdk-dotnet.git
dotnet add reference ./haiven-sdk-dotnet/Haiven.Sdk/Haiven.Sdk.csproj
```

## Quickstart

Install `Haiven.Sdk` as shown under [Install](#install). The core never opens
a browser or makes an HTTP request by itself, so a plain .NET host supplies
three small adapters: `SystemBrowser`, `HttpHandoffExchange` and
`HttpRestTransport` at the end of this `Program.cs`. The URLs are production's.
Ask the Haiven app owner for your public integration id and a channel id, and
register the redirect URI on the integration exactly, port included. Use the IP
literal `127.0.0.1`, not `localhost`.

```csharp
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Rest;

var http = new HttpClient();
var redirectUri = new Uri("http://127.0.0.1:47615/haiven/callback");

// Sign in: open Haiven's sign-in page, then receive the redirect on loopback.
var login = new HandoffCoordinator(
    new HandoffOptions(
        new Uri("https://haiven.gg/connect/game"),
        new Uri("https://haiven.gg/v0/auth/handoff/exchange"),
        "your-public-integration-id",
        redirectUri),
    new SystemBrowser(),
    new MemoryHandoffTransactionStore(),
    new MemoryHandoffTokenStore(), // development only: forgets the credential on exit
    new HttpHandoffExchange(http),
    new ImmediateHandoffDispatcher());

var credential = login.Resume(); // null when nothing valid is saved
if (credential is null)
{
    using var listener = LoopbackHandoffListener.Start(redirectUri);
    credential = await login.LoginAsync(listener);
}
string Token() => credential.AccessToken;

// One REST call.
var rest = new RestApi(new HttpRestTransport(http, new Uri("https://haiven.gg/v0/"), Token));
var tags = await rest.GetAppsTagsAsync();
Console.WriteLine($"{tags?.Count ?? 0} public tags");

// One realtime message: queued until the session has authenticated.
var chat = new HaivenChat();
chat.OutgoingChanged += message => Console.WriteLine($"chat send: {message.State}");
var socket = new SystemNetSocket(); // raises events on a background thread
var session = new HaivenSessionCore(socket, Token);
chat.Attach(session);
session.Authenticated += () => Console.WriteLine("realtime ready");
await socket.ConnectAsync(new Uri("wss://haiven.gg/v0/ws"));
session.Opened(); // sends the auth frame
chat.Send("your-channel-id", "gg");
Console.ReadLine(); // keep the process, and the socket, alive

sealed class SystemBrowser : IHandoffBrowser
{
    public void Open(Uri uri) => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
}

sealed class HttpHandoffExchange : IHandoffHttpTransport
{
    private readonly HttpClient http;
    public HttpHandoffExchange(HttpClient http) => this.http = http;

    public async Task<HandoffExchangeResponse> ExchangeAsync(
        Uri exchangeUri, HandoffExchangeRequest request, CancellationToken cancellationToken)
    {
        using var body = new StringContent(request.ToFormBody(), Encoding.UTF8, "application/x-www-form-urlencoded");
        using var response = await http.PostAsync(exchangeUri, body, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HandoffException($"Handoff exchange failed with HTTP {(int)response.StatusCode}.");
        return HandoffExchangeResponse.ParseJson(text);
    }
}

sealed class HttpRestTransport : IRestApiTransport
{
    private readonly HttpClient http;
    private readonly Uri baseUri;
    private readonly Func<string> token;

    public HttpRestTransport(HttpClient http, Uri baseUri, Func<string> token)
    {
        this.http = http;
        this.baseUri = baseUri;
        this.token = token;
    }

    public Uri Url(string path) => new Uri(baseUri, path.TrimStart('/'));

    public async Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), Url(request.Path));
        var accessToken = token();
        if (!string.IsNullOrEmpty(accessToken))
            message.Headers.Authorization = new("Bearer", accessToken);
        if (request.Body != null)
            message.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/json");
        using var response = await http.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HaivenRestException((int)response.StatusCode, body);
        return body.Length == 0 ? null : body;
    }
}
```

- `MemoryHandoffTokenStore` forgets the credential when the process exits, so
  every launch signs in again. To keep players signed in, pass your own
  `IHandoffCredentialStore` backed by the platform's secure storage.
- `Authenticated` is the ready signal; an open socket is not. Sends made before
  it are queued, not lost.
- `SystemNetSocket` raises its events on a background thread. A game marshals
  them to its main thread, for example through `SynchronizationContextSocket`.
- There is no refresh token. After a REST `401` or a WebSocket `auth.expired`,
  call `RestartLogin` and sign in again.
- Unity projects use the Unity package's adapters (`UnityHandoffBrowser`,
  `UnityRestApiTransport`, `UnitySocket`) instead of these three.

## The question

**Does the outbound envelope's discriminant survive the crossing into C#, and
does the binding compile against the surface Unity actually supports?**

The two server frames are told apart by the **presence** of `success`, not by its
value (F10). That is free in TypeScript, where `undefined` is a value a field can
have. C# has no undefined, and every JSON reader in the ecosystem has an opinion
about what a missing field becomes — the usual one being the type's default,
which for `bool` is `false`.

If that opinion wins, every event reads as a failed reply. Worth knowing before
three more bindings are written against the same discriminant.

## What is here

- `Haiven.Sdk/` — the binding, targeting **netstandard2.1**, which is Unity's
  supported surface. Pinned deliberately: a library that compiles under net10.0
  tells you nothing about whether it compiles for a game.
- `Haiven.Sdk.Tests/` — a console runner, matching how this repository already
  verifies its emitted C#. One fewer dependency, and the failure output is the
  assertion rather than a harness's rendering of it.
- `Haiven.Sdk.AotProbe/` — a net10.0 app that roots `Haiven.Sdk` and runs
  ILC over it, because the netstandard2.1 build runs no trim or AOT analysis.
  `make check-aot` fails on any IL2xxx/IL3xxx warning not in its
  `aot-baseline.txt`, and on any baseline entry that no longer fires. The
  baseline is intentionally empty for the built-in generated codec.
- `Haiven.Sdk/Handoff.cs` — the engine-neutral game login coordinator. Browser
  launch, callback delivery, transient verifier state, token ownership, HTTP,
  clock, and completion dispatch are injected so engine packages can supply
  their actual platform boundaries. `Haiven.Sdk/HandoffLoopback.cs` adds the
  desktop loopback redirect (`LoopbackHandoffListener`, `LoginAsync`) for
  hosts without a custom scheme.
- `Haiven.Sdk/RestApi.g.cs` — generated by `codegen/emit_rest.py`; do not
  edit. One `…Async` method per published REST operation over an injected
  `IRestApiTransport`, plus a DTO per OpenAPI schema in `Haiven.Sdk.Rest`, each
  read and written by a generated method in `RestJson` (no reflection). SSO initiation and the OAuth callback answer only with a
  redirect, so they are `…Url` builders returning a `Uri` to open in a browser.
  The transport owns base URL, bearer, and HTTP, and throws on a non-2xx
  response. `Haiven.Sdk.Rest` mirrors every schema, including a
  `HandoffCredential` distinct from `Haiven.Sdk.HandoffCredential`, so import it
  under an alias when both namespaces are in scope.
- `Haiven.Sdk/PlayerAccess.cs` — thin Phase 1 helpers. `GetGameAsync` delegates to the generated game-access route and leaves a no-body success and any `HaivenRestException` unchanged. `FounderStatus` finds the open `founder` code without treating it as subscriber eligibility. Unity receives the same file through `make vendor-unity`.
- `Haiven.Sdk/Rewards.cs` — subscriber-reward claims over the
  generated REST binding. `RewardClaims.ClaimAsync` keeps a UUIDv7 claim id until
  the claim gets a final answer, so retries reuse it and never claim twice. It
  maps each documented refusal to a finite `RewardClaimResult`, using the
  error code a transport reports by throwing `HaivenRestException`.
  `Save` records a claim id before the player links and does not post it.
  Pass an `IRewardClaimIntentStore` (`MemoryRewardClaimIntentStore` or
  `FileRewardClaimIntentStore`) to keep that id across launches.
  `SubmitSavedAsync` posts each saved intent on the user claim route, and
  `Follow` does that when the handoff completes. Grant loot only from the
  `Claimed` event (`RewardClaimResult.Claimed`). `ClaimSettled` also reports
  refusals. `Clear` leaves saved intents in place.
  `GrantAsync` / `ConfirmAsync` are the game-server routes, which take an
  integration key.
- `Haiven.Sdk/WsApi.g.cs` — generated by `codegen/emit_ws.py` from the
  AsyncAPI description; do not edit. `new WsApi(session)` gives one typed method
  per client WebSocket request over `HaivenSessionCore.Send` (plus
  `Decode…Reply` for its reply), `WsEventRouter.On…` subscribes typed handlers to
  server events, including the OAuth identity-link pair, and `Dispatch` returns
  false for replies and unknown types. `WsTypes` holds the wire types.
- `Haiven.Sdk.Contracts` — the client contract types (frames, events,
  payloads), generated by `codegen/emit_contracts.py` into
  `codegen/emitted-contracts/` and compiled by path into `Haiven.Sdk` and
  `Haiven.Sdk.Core`. A presence discriminant such as `WsServerReplyV0.Success`
  is `bool?` even though the schema requires it. `Haiven.Sdk/ContractsJson.g.cs`
  (generated by `codegen/emit_cs_json.py`) reads and writes every one of them;
  the generated `WsApi` decoders call it.
- `Haiven.Sdk/Json.cs`, `Haiven.Sdk/BuiltInJson.cs` — the JSON seam (below).
- `Haiven.Sdk/Session.cs` — the shared game-engine session state machine. Unity
  and Godot inject their own socket adapters; first-frame authentication,
  pre-auth queuing, frame classification, and same-socket re-auth live here.

## What has been answered

**The discriminant survives, but only if the field is nullable and the reader is
asked what the document contains.** `bool?` plus `TryGetProperty` keeps "absent"
distinct from "false". A non-nullable `bool` collapses them, and the case it
destroys is not a rare one — it is every refused request. Neutering `IsReply` to
read the *value* rather than the presence fails exactly one assertion: *a refused
reply is still a reply*.

**Absent and empty are both on the wire, and both mean something.** A
server-initiated reply sends `requestId: ""` — present, empty — while an event
omits the field entirely. A reader that mapped missing to `""` would erase the
difference between "answers no request" and "is not an answer".

**The transport has to be injected, and Unity's targets are why.** A desktop or
mobile IL2CPP build can use `System.Net.WebSockets`; a WebGL build cannot — no
threads, no socket stack — so it needs a `.jslib` bridge into the browser's own
WebSocket. A binding naming a concrete class would compile everywhere and fail on
the one target games ship to most.

**`System.Text.Json` is not in the netstandard2.1 box, so the SDK no longer
needs it.** The SDK reads and writes JSON through `IHaivenJsonCodec`: a parsed
node it can ask whether a member was present (`IHaivenJsonNode`), and a
forward-only writer (`IHaivenJsonWriter`). Every wire type has a generated read
and write method over those, so nothing is constructed or enumerated by
reflection, and a trimmed, NativeAOT or IL2CPP build keeps what it uses. Which
library sits behind the seam is configurable:

- **Built-in (default):** `BuiltInJsonCodec`, a small RFC 8259 reader and writer
  in the SDK itself. No dependencies. It refuses to write or read a type the SDK
  did not generate unless the type implements `IHaivenJsonWritable` (writing),
  rather than reflect over it.
- **System.Text.Json (opt-in):** reference `Haiven.Sdk.SystemTextJson` (or
  `Haiven.Sdk.Core.SystemTextJson` for the engine hosts) and select it. It also
  serializes a game's own payload types reflectively, which is the one part of
  either codec that is not AOT-safe.

Select a codec process-wide with `HaivenJson.Default = SystemTextJsonCodec.Instance;`
before the first session, or per instance: `new HaivenSessionCore(socket, tokens,
codec: codec)` and `new RestApi(transport, codec)`. Both constructors take the
codec as an ordinary parameter, so a DI container can supply it. Either way the
same generated code decides what a payload means; `make check-csharp` runs the
whole test runner once under each codec.

A required member that is missing, a value of the wrong JSON kind, or a `null`
where the C# property cannot hold one is a `HaivenJsonException` naming the
field, as in the C++ SDK. (The reflective reader this replaced left a required
`string` as `null` behind a non-nullable type.)

**The login handoff needs no game secret and creates no refresh lifecycle.** The
core generates 256-bit verifier and state values with the platform CSPRNG,
derives an S256 challenge, validates the exact callback and state before a
form-encoded exchange, and removes the verifier before the network request. It
stores only the returned opaque bearer, token type, and absolute expiry through
the host's token-store interface. A failed, cancelled, expired, or replayed
callback cannot reuse the transaction.
`RestartLogin` clears that store and begins a fresh handoff for metadata expiry,
REST `401`, WebSocket `auth.expired`, a `1008 credential_revoked` close
(`CredentialRevoked`), or a handshake refused with `1008 unauthorized`
(`CredentialRefused`); none of those events enters a web refresh-token path. A null `expires_in` (a durable credential) gives a
null `ExpiresAt`. A store that also implements `IHandoffCredentialStore.Load`
lets `Resume()` reuse the saved credential at start, so the browser opens only
when nothing valid is saved; the included `MemoryHandoffTokenStore` is for
development only. `HandoffCredential.ExpiresAt` and
`HandoffExchangeResponse.ExpiresInSeconds` became nullable for this, a source-
and binary-breaking change for code that reads them as non-nullable values;
`HasExpired(now)` replaces direct comparisons.

**The second engine consumer forced the reusable boundary to include session
semantics, not just DTOs and a socket interface.** The core now owns auth order,
queuing, correlation, expiry, and re-authentication while engine packages own
only connection establishment, polling/callback dispatch, and platform UI.
Godot's .NET addon references this same project rather than copying it.

## Headless chat

`HaivenChat` (`Haiven.Sdk/Chat.cs`) is chat without UI: send, receive and
typing over the session core, for a game to put its own UI on.

```csharp
var chat = new HaivenChat();
chat.MessageReceived += message => ui.Append(message);
chat.OutgoingChanged += sent => ui.Mark(sent.ClientMessageId, sent.State);
chat.Attach(session);                       // and again after every reconnect
var sent = chat.Send(channelId, "gg");      // never throws on a network failure
```

- **A session is one socket, so the chat object outlives sessions.**
  `Attach` the session the game opens after each reconnect.
- **Every send carries a client message id.** A send still unanswered when its
  socket closes becomes `Unconfirmed`, and the next `Attach` resends it under
  the same id. The server answers a resend with the original message
  (`clientMessageId` deduplication), so a lost
  reply never turns into a lost or doubled message.
  **Merge order:** that server change must be live before a build relies on it,
  otherwise a resend after a lost reply can duplicate.
- **States:** `Sending`, `Unconfirmed`, `Accepted` (`allowed`/`warned`), `Held`
  (`pending`/`review_pending`), `Removed` (`hidden`/`blocked`), and `Refused`
  (an error code, final).
  - **Unknown states:** a moderation state minted after the build reads as
    `Held`, never as visible.
- **Typing is best effort**, and is never resent.

**Session chat.** A P2P session owns one moderated chat channel. The game
service returns its id as `chatChannelId` in the create and join replies, and
the SDK keeps it on the admission as
`HaivenSessionAdmission.ChatChannelId`. Host and guests get the same id.
`SessionChatChannelFilter` (`Haiven.Sdk/SessionChat.cs`) is the session's view
of a `HaivenChat`:

```csharp
chat.Attach(session);                                 // the session given to p2p.Attach
await p2p.CreateAsync(options);                       // or p2p.JoinAsync(...)
var sessionChat = new SessionChatChannelFilter(chat, p2p); // marked open again on resume
sessionChat.MessageReceived += message => ui.Append(message);
var sent = sessionChat.Send("gg");                    // sent on p2p.Room.ChatChannelId
sessionChat.Dispose();                                // when the session ends
```

- **`HaivenChat` must be attached to the same `HaivenSessionCore` as
  `p2p.Attach`,** and again to the same one after every reconnect. The game
  service accepts a message on a session channel only from the socket recorded
  on the player's participation. A send from any other socket, even another socket of
  the same player, is refused with `not_channel_member`.
- **Only the session's channel reaches the game.** `MessageReceived`,
  `MessageUpdated`, `MessageDeleted` and `Typing` fire only for events on the
  admission's channel; events on every other channel are dropped.
  `OutgoingChanged` stays on `HaivenChat`.
- **`Send` and `SendTyping` go to the admission's channel.** They throw
  `ObjectDisposedException` after `Dispose`.
- **The filter marks the channel open.** It sends `chat.channel.active` for
  the admission's channel when it is created (or on `HaivenChat.Attach` when
  the chat has no session yet). The game service delivers `chat.typing` only to the
  users who have the channel open. It forgets the mark when the socket closes,
  and refuses it from a new socket until the participation has resumed there.
  Build the filter from the `HaivenP2PSession` (`new
  SessionChatChannelFilter(chat, p2p)`) and it marks the channel again on
  `Resumed`. Built from an admission, it cannot see the resume, so call
  `chat.SetActiveChannel(sessionChat.ChannelId)` from `p2p.Resumed` yourself.
  `SendTyping` reports `isTyping: true`.
- **Built from the `HaivenP2PSession`, sends wait for a resume.** After a drop,
  the game service keeps the participation, and with it the channel membership, on
  the old connection until `session.resume` moves it. A `chat.send` that
  reached the new socket first would be refused with `not_channel_member`, and
  a refusal is final. So while the session is resuming, or its socket is
  closed, sends on its channel stay `Unconfirmed`, `HaivenChat.Attach` does not
  resend them, and they go out on `HaivenP2PSession.Resumed`. If the session
  ends instead, they go out then and get the server's answer. Typing on the
  channel is dropped while sends are held. A re-auth after `auth.expired` keeps
  the socket, so it holds nothing: the socket queues sends and typing until
  `auth.success`. Attach each new game socket to both `chat` and `p2p`, in
  either order: sends go out only on the socket the participation is bound to. Build it after `CreateAsync` or `JoinAsync` returns; before that it
  throws `InvalidOperationException`. The filter built from an admission or a
  channel id does not wait.
- **`Dispose` unsubscribes from `HaivenChat`** and from the P2P session,
  releases its hold on sends, and stops marking the channel open on later
  sessions. Another filter built on the same session keeps its own hold. A new
  session means a new admission and a new filter.
- **An admission without a channel id is refused.** The constructor throws
  `ArgumentException` when `ChatChannelId` is empty, which is what a reply
  from a game service without session chat produces.
- The filter is in the .NET core and vendored into Unity. JavaScript
  (`SessionChatChannelFilter`), C++ (`Haiven::FSessionChatChannelFilter`),
  Unreal (`UHaivenSessionChat`) and Godot GDScript
  (`HaivenSessionChatChannelFilter`) have the same filter built from the
  admission's channel id. They have no P2P session controller, so none of them
  holds sends for a resume. Roblox does not have it yet.

Not here yet:
- **Catch-up after a reconnect** (`afterMessageId`) and the author's late
  moderation event (`chat.message.moderated`) wait until the contracts
  publish them for codegen.
- **The Unity starter UI and sample scene** need the editor host.

## P2P sessions

`HaivenP2PSession` (`Haiven.Sdk/P2PSessions.cs`) is the P2P controller of
the game service's sessions: one participation in one session, without a
WebRTC library or a networking stack. Other targets port it against the same
behaviour; Godot .NET compiles it as is.

```csharp
var p2p = new HaivenP2PSession(new HaivenSessionsClient(rest, gameId));
p2p.Attach(session, connectionId);          // and again after every reconnect
p2p.Ended += (reason, code) => game.StopNetworking();
var room = await p2p.CreateAsync(new HaivenSessionCreateOptions(maxMembers: 4));
transport.Start(p2p.Signaling, p2p.IceOptions);   // the game's transport adapter
p2p.Peers = transport;                      // IHaivenPeerRoster: connected users, drop
// every frame:
p2p.Tick();
```

- **Signaling** (`HaivenSessionSignaling`) is `p2p.signal` over the game socket:
  each message is acknowledged, resent 1, 2 and 4 seconds apart, deduplicated
  and handed on in order. Each guest offer takes the next `epoch`, and the host
  starts a new attempt only for a higher one, so a delayed offer cannot replace
  a newer attempt. An offer or answer is cut into parts that each fit
  the game service's 4,096-byte `data` limit, at most 16 parts and 32 KiB, and the
  receiver hands it on whole. A `rate_limited` reply costs no retry. A message
  the server refuses with `invalid_payload`, or an SDP too large to send, is
  not sent again and is reported through `Refused`.
- **A dropped socket** keeps the session for 30 seconds. `Attach` the next
  socket and the controller sends `session.resume` with the participation's
  resume token, which stays in memory and is never logged. A close with
  `1013 auth_unavailable` resumes the same way. Any `1008` close, or a REST
  `401`, ends the session as `SignedOut` or `SocketRefused`, because a new
  socket with the same credential would be refused too.
- **A lost admission reply** reaches the caller as the transport's exception.
  The SDK does not resend it. Calling `CreateAsync` or `JoinAsync` again with
  the same arguments on the same socket repeats the same `admissionId` and
  resume token, and the game service returns its first answer.
- **The host** reads the roster every 30 seconds and right after a resume, and
  drops through `Peers` anyone the server no longer lists or reports gone.
- **The transport seam** is `IHaivenPeerTransport` behind `HaivenPeerLink`, which
  applies the 1,200-byte, 64 KiB and 1 MiB limits; `PeerFraming` carries a
  game's reliable and unreliable messages over the two data channels. The Unity
  package implements the seam on `com.unity.webrtc`.
- Everything runs on the thread that calls `Tick`.

`HaivenPeerConnections` (`Haiven.Sdk/PeerConnections.cs`) owns the offer/answer exchange over `IHaivenPeerTransport`, buffers at most 64 candidates while a description is pending, and fails an attempt after 20 seconds without both channels open. The deadline starts on the first accepted fragment, so an incomplete SDP also expires. Only the guest offers. The adapter must verify DTLS against the remote SDP before raising `ChannelOpened`; a connected event requires both channels and the applied signaled description. The Unity Netcode sample delegates attempts to this core and keeps its NGO client ids and message framing.

After admission, an engine supplies its transport factory and drives both controllers:

```csharp
var peers = new HaivenPeerConnections(p2p.Signaling!, CreateWebRtcTransport)
{
    Connection = p2p.IceOptions!,
};
p2p.Peers = peers;
peers.Connected += p2p.ReportPeerConnected;
p2p.IceOptionsChanged += options => peers.Connection = options;
p2p.Ended += (_, _) => peers.Dispose();
peers.Start(room.Hosting, room.HostUserId);
// every frame:
p2p.Tick();
peers.Tick();
```

`Started` supplies each new user's `HaivenPeerLink` for sending and receiving game messages. `Disconnected` reports a failed or closed attempt once, with a fixed reason rather than SDP or library exception text. A signaling timeout or refusal ends a pending attempt, but keeps an already connected peer open. `DropUser` closes a pending or open peer. `Dispose` stops signaling and closes all attempts; late async results are ignored. `Detach` transfers a removed link to an adapter that needs to send a goodbye before disposing it.

`P2PConformance.cs` runs 26 `gaming-p2p-client/v0` scenarios against the session and peer controllers with scripted REST, a game socket, a fake transport and a manual clock. They cover 14 of the 15 `SDK-P2P` requirements; SDK-P2P-15 has no scenario form. `PeerConnectionsTests` also covers replacement, stale async completions, restart, the 64-candidate boundary, rejection of an answer from another user, and signaling timeouts and `invalid_payload` refusals on pending and connected peers in both roles. The Unity tests `PeerWithAnotherFingerprintIsRefused` and `HostRefusesGuestWithAnotherFingerprint` check real DTLS refusal in each direction. Fake transport scenarios prove the controller's response to that refusal, not the WebRTC library.

## Running it

```
make check        # from the repository root
```

`check-csharp` and `check-conformance-dotnet` skip loudly without the pinned
`dotnet`.

## Consumers

`Haiven.Sdk.Core/` builds the same sources as the netstandard2.1 core that engine
adapters consume. The Unity package lives in [`../unity`](https://github.com/haiven-dev/haiven-sdk-unity/blob/main/README.md) and
the Godot .NET flavour in `../godot`; both reference this
project rather than copying it.

## License

Haiven's source in this repository is licensed under the Apache License,
Version 2.0 (SPDX `Apache-2.0`). The terms are in [LICENSE](LICENSE), and each
library project records the SPDX identifier as its
`PackageLicenseExpression`. Contributions are accepted under that license, as
[CONTRIBUTING.md](CONTRIBUTING.md) says.

<!-- features:start (generated by codegen/features.py; do not edit) -->

### Feature coverage

| Family | .NET | Unity |
|---|:---:|:---:|
| Envelope | ~ | ~ |
| Session lifecycle | ~ | ~ |
| Request recovery | ✅ | ✅ |
| Open vocabularies | ~ | ~ |
| REST | ○ | ○ |
| Game login handoff | ○ | ○ |
| P2P sessions | ~ | ~ |
| Credential lifecycle | ~ | ~ |
| API surface | ~ | ~ |
| Product | ~ | ~ |
| Developer experience | — | ~ |

✅ shipped · `↑` inherited from the language SDK this target ships, proven only in that SDK's runner and not inside the engine · `~` partial · `—` absent · `○` not yet proven by any scenario · `⊘` not applicable. A family cell is ✅ only when every counted row is ✅, and `↑` when every counted row is ✅ or `↑`; `MAY` requirements and rows marked optional are not counted. Every row: the full Haiven SDK feature matrix.

<!-- features:end -->
