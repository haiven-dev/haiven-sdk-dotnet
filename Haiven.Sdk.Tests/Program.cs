// A console runner rather than a test framework, matching how this repository
// already verifies its emitted C#: one fewer dependency, and the failure output
// is the assertion rather than a harness's rendering of it.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Tests;
using Haiven.Sdk.Json;
using Haiven.Sdk.Json.SystemTextJson;
// Aliased rather than imported: Haiven.Sdk.Rest mirrors every OpenAPI schema,
// including HandoffCredential, which Haiven.Sdk also defines for the handoff flow.
using IRestApiTransport = Haiven.Sdk.Rest.IRestApiTransport;
using LoginRequest = Haiven.Sdk.Rest.LoginRequest;
using PostAuthHandoffExchangeBody = Haiven.Sdk.Rest.PostAuthHandoffExchangeBody;
using RestApi = Haiven.Sdk.Rest.RestApi;
using RestRequest = Haiven.Sdk.Rest.RestRequest;

var failures = new List<string>();

// The whole runner executes once per JSON codec (`--codec builtin|system-text-json`,
// builtin by default): every assertion below holds under either, because the
// generated readers and writers are the same code over both.
var codecName = args.SkipWhile(arg => arg != "--codec").Skip(1).FirstOrDefault() ?? "builtin";
HaivenJson.Default = codecName switch
{
    "builtin" => BuiltInJsonCodec.Instance,
    "system-text-json" => SystemTextJsonCodec.Instance,
    _ => throw new ArgumentException($"unknown codec {codecName}"),
};

void Check(string what, bool condition)
{
    if (!condition) failures.Add(what);
}

// The discriminant, in the three shapes that matter.
var accepted = ServerFrame.Parse("""{"type":"pong","requestId":"rq-1","success":true}""");
Check("an accepted reply is a reply", accepted.IsReply);
Check("and reports its outcome", accepted.Success == true);
Check("and carries the id it answers", accepted.RequestId == "rq-1");

// The case a non-nullable bool destroys. A refused request is still a reply, and
// `success: false` must not read as "no success field, therefore an event".
var refused = ServerFrame.Parse(
    """{"type":"chat.send","requestId":"rq-2","success":false,"error":"rate_limited"}""");
Check("a refused reply is still a reply", refused.IsReply);
Check("with the outcome it reported", refused.Success == false);
Check("and a code rather than prose", refused.Error == "rate_limited");

var evt = ServerFrame.Parse("""{"type":"chat.message","payload":{"id":"m1"}}""");
Check("an event is not a reply", !evt.IsReply);
Check("because the field is absent, not false", evt.Success is null);
Check("an event carries a payload", evt.Payload != null);

// Absent and empty are different, and the wire uses both. A server-initiated
// reply sends requestId "" -- present, empty -- while an event omits the field
// entirely. A reader that mapped missing to "" would erase that.
var announced = ServerFrame.Parse("""{"type":"auth.expired","requestId":"","success":false}""");
Check("a server-initiated reply has an empty id", announced.RequestId == "");
Check("an event has no id at all", evt.RequestId is null);
Check("so empty and absent stay distinguishable", announced.RequestId != evt.RequestId);

// The same session core is consumed by Unity and the Godot .NET adapter. The
// socket is fake here so the protocol state machine remains headless and the
// engine wrappers only pay for their transport and dispatch mechanics.
var socket = new RecordingSocket();
var currentToken = "token-one";
var requestCounter = 0;
var session = new HaivenSessionCore(
    socket,
    () => currentToken,
    () => $"session-{++requestCounter}");
var rawFrames = new List<ReceivedFrame>();
var replies = new List<ReceivedFrame>();
var events = new List<ReceivedFrame>();
var authenticatedCount = 0;
var expiredCount = 0;
session.FrameReceived += rawFrames.Add;
session.ReplyReceived += replies.Add;
session.EventReceived += events.Add;
session.Authenticated += () => authenticatedCount++;
session.Expired += () => expiredCount++;

Check("HTTP 401 clears the credential", HaivenCredentialLifecycle.ForHttpStatus(401) == HaivenCredentialAction.Clear);
Check("HTTP 403 retains the credential", HaivenCredentialLifecycle.ForHttpStatus(403) == HaivenCredentialAction.Retain);
Check("transport failure retains the credential", HaivenCredentialLifecycle.ForHttpStatus(0) == HaivenCredentialAction.Retain);
Check("initial unauthorized clears the credential", HaivenCredentialLifecycle.ForSocketClose(
    HaivenSessionState.Pending, 1008, "unauthorized") == HaivenCredentialAction.Clear);
Check("live unauthorized retains pending proof", HaivenCredentialLifecycle.ForSocketClose(
    HaivenSessionState.Authenticated, 1008, "unauthorized") == HaivenCredentialAction.Retain);
Check("live revocation clears the credential", HaivenCredentialLifecycle.ForSocketClose(
    HaivenSessionState.Authenticated, 1008, "credential_revoked") == HaivenCredentialAction.Clear);
Check("ordinary close retains the credential", HaivenCredentialLifecycle.ForSocketClose(
    HaivenSessionState.Authenticated, 1006, "transport") == HaivenCredentialAction.Retain);
Check("same-socket refusal retries fresh before clearing", HaivenCredentialLifecycle.ForAuthenticationFailure("unauthorized")
    == HaivenCredentialAction.RetryFreshSocket);
Check("same-socket invalid_token retries fresh before clearing", HaivenCredentialLifecycle.ForAuthenticationFailure("invalid_token")
    == HaivenCredentialAction.RetryFreshSocket);
Check("an unrelated auth.failure code retains the credential", HaivenCredentialLifecycle.ForAuthenticationFailure("rate_limited")
    == HaivenCredentialAction.Retain);
Check("an auth.failure without a code retains the credential", HaivenCredentialLifecycle.ForAuthenticationFailure(null)
    == HaivenCredentialAction.Retain);

var queued = session.Send("chat.send", new Dictionary<string, object?> { ["body"] = "queued" });
Check("application frames queue before the socket opens", socket.Sent.Count == 0);
session.Opened();
Check("opening sends auth as the first frame", socket.Sent.Count == 1 && JsonField(socket.Sent[0], "type") == "auth");
Check("auth reads the current token owner", JsonField(socket.Sent[0], "payload") == "token-one");

socket.Receive("""{"type":"auth.success","requestId":"session-2","success":true}""");
Check("auth.success transitions the shared core", session.State == HaivenSessionState.Authenticated && authenticatedCount == 1);
Check("the queued application frame flushes after auth", socket.Sent.Count == 2 && JsonField(socket.Sent[1], "requestId") == queued.RequestId);
session.Send("ping");
Check("a null payload is omitted from client frames", !socket.Sent[2].Contains("\"payload\"", StringComparison.Ordinal));

socket.Receive("""{"type":"chat.send","requestId":"session-1","success":false,"error":"rate_limited"}""");
socket.Receive("""{"type":"chat.message","payload":{"id":"m1"}}""");
Check("the shared core preserves refused replies", replies.Count == 1 && replies[0].Frame.Success == false);
Check("and keeps events out of the reply arm", events.Count == 1 && events[0].Frame.Type == "chat.message");
Check("the raw arm observes every frame before classification", rawFrames.Count == 3);

currentToken = "token-two";
socket.Receive("""{"type":"auth.expired","requestId":"","success":false}""");
Check("auth.expired transitions once", session.State == HaivenSessionState.Expired && expiredCount == 1);
Check("re-auth stays on the same socket and rereads token ownership", socket.Sent.Count == 4 && JsonField(socket.Sent[3], "payload") == "token-two");

socket.Receive("""{"type":"auth.success","requestId":"session-4","success":true}""");
session.Close();
socket.Finish(1000, "done");
Check("the shared core closes through the injected socket", socket.CloseCalls == 1 && session.State == HaivenSessionState.Closed);
Check("the caller's own close is a transport close", session.CloseKind == HaivenSessionCloseKind.Transport);

// A close says what it means for the credential (haiven-api websocket.md, Game
// sockets): a refused or revoked credential needs a new sign-in, a dropped
// connection keeps it, and no 1008 is worth reconnecting into.
{
    var closeRevokedSocket = new RecordingSocket();
    var revoked = new HaivenSessionCore(closeRevokedSocket, () => "game-credential", () => "revoked-1");
    HaivenSessionCloseKind? seenFromClosed = null;
    revoked.Closed += (_, _) => seenFromClosed = revoked.CloseKind;
    revoked.Opened();
    closeRevokedSocket.Receive("""{"type":"auth.success","requestId":"revoked-1","success":true}""");
    Check("an open session has no close kind", revoked.CloseKind is null);
    closeRevokedSocket.Finish(1008, "credential_revoked");
    Check("a revoked credential is classified before Closed is raised", seenFromClosed == HaivenSessionCloseKind.CredentialRevoked);

    var closeRefusedSocket = new RecordingSocket();
    var closeRefusedSession = new HaivenSessionCore(closeRefusedSocket, () => "bad", () => "refused-1");
    closeRefusedSession.Opened();
    closeRefusedSocket.Finish(1008, "unauthorized");
    Check("a refused handshake is a refused credential", closeRefusedSession.CloseKind == HaivenSessionCloseKind.CredentialRefused);

    var closeTable = new (int Code, string Reason, HaivenSessionCloseKind Kind)[]
    {
        (1008, "unauthorized", HaivenSessionCloseKind.CredentialRefused),
        (1008, "credential_revoked", HaivenSessionCloseKind.CredentialRevoked),
        (1008, "parental_consent_denied", HaivenSessionCloseKind.Policy),
        (1008, "minted_after_this_build", HaivenSessionCloseKind.Policy),
        (1000, "", HaivenSessionCloseKind.Transport),
        (1001, "going away", HaivenSessionCloseKind.Transport),
        (1006, "", HaivenSessionCloseKind.Transport),
        (1000, "credential_revoked", HaivenSessionCloseKind.Transport),
    };
    foreach (var (code, reason, kind) in closeTable)
    {
        Check($"close {code} {reason} classifies as {kind}", HaivenSessionClose.Classify(code, reason) == kind);
        Check($"close {code} {reason} may reconnect only as a transport close",
            HaivenSessionClose.MayReconnect(kind) == (kind == HaivenSessionCloseKind.Transport));
    }

    Check("a refused credential requires a new sign-in", HaivenSessionClose.RequiresSignIn(HaivenSessionCloseKind.CredentialRefused));
    Check("a revoked credential requires a new sign-in", HaivenSessionClose.RequiresSignIn(HaivenSessionCloseKind.CredentialRevoked));
    Check("a policy refusal keeps the credential", !HaivenSessionClose.RequiresSignIn(HaivenSessionCloseKind.Policy));
    Check("a transport close keeps the credential", !HaivenSessionClose.RequiresSignIn(HaivenSessionCloseKind.Transport));
}

var rejectedSocket = new RecordingSocket();
var rejectedSession = new HaivenSessionCore(rejectedSocket, () => "rejected", () => "rejected-1");
var rejectedActions = new List<HaivenCredentialAction>();
rejectedSession.CredentialActionRequired += rejectedActions.Add;
rejectedSession.Opened();
rejectedSocket.Finish(1008, "unauthorized");
Check("an initial unauthorized close asks the host to clear", rejectedActions.SequenceEqual(
    new[] { HaivenCredentialAction.Clear }));

// ADR 0041: a verifier-refused credential answers same-socket re-auth with
// invalid_token. It is still retried on a fresh socket; only that handshake's
// 1008 unauthorized clears it, and 1013 auth_unavailable retains it.
var invalidTokenSocket = new RecordingSocket();
var invalidTokenCounter = 0;
var invalidTokenSession = new HaivenSessionCore(
    invalidTokenSocket,
    () => "same-token",
    () => $"invalid-{++invalidTokenCounter}");
var invalidTokenActions = new List<HaivenCredentialAction>();
invalidTokenSession.CredentialActionRequired += invalidTokenActions.Add;
invalidTokenSession.Opened();
invalidTokenSocket.Receive("""{"type":"auth.success","requestId":"invalid-1","success":true}""");
invalidTokenSocket.Receive("""{"type":"auth.expired","requestId":"","success":false}""");
invalidTokenSocket.Receive("""{"type":"auth.failure","requestId":"","success":false,"error":"invalid_token"}""");
Check("same-socket invalid_token asks for one fresh-socket retry", invalidTokenActions.SequenceEqual(
    new[] { HaivenCredentialAction.RetryFreshSocket }));
Check("the fresh-socket retry clears on 1008 unauthorized", HaivenCredentialLifecycle.ForSocketClose(
    HaivenSessionState.Pending, 1008, "unauthorized") == HaivenCredentialAction.Clear);
Check("the fresh-socket retry retains on 1013 auth_unavailable", HaivenCredentialLifecycle.ForSocketClose(
    HaivenSessionState.Pending, 1013, "auth_unavailable") == HaivenCredentialAction.Retain);

var refusedSocket = new RecordingSocket();
var refusedCounter = 0;
var refusedSession = new HaivenSessionCore(
    refusedSocket,
    () => "same-token",
    () => $"refused-{++refusedCounter}");
using var refusedChat = new HaivenChat(() => "same-socket-message");
refusedChat.Attach(refusedSession);
var refusedActions = new List<HaivenCredentialAction>();
refusedSession.CredentialActionRequired += refusedActions.Add;
refusedSession.Opened();
refusedSocket.Receive("""{"type":"auth.success","requestId":"refused-1","success":true}""");
var refusedPending = refusedChat.Send("ch-1", "survive same-socket auth failure");
refusedSocket.Receive("""{"type":"auth.expired","requestId":"","success":false}""");
refusedSocket.Receive("""{"type":"auth.failure","requestId":"","success":false,"error":"unauthorized"}""");
Check("same-socket unauthorized asks for one fresh-socket retry", refusedActions.SequenceEqual(
    new[] { HaivenCredentialAction.RetryFreshSocket }));
refusedSession.Close();
Check("fresh-socket recovery explicitly closes the refused socket", refusedSocket.CloseCalls == 1);
refusedSocket.Finish(1000, "fresh socket");
Check("the refused session reaches Closed before replacement", refusedSession.State == HaivenSessionState.Closed);
Check("closing marks its unanswered chat send unconfirmed", refusedPending.State == OutgoingChatState.Unconfirmed);

var replacementSocket = new RecordingSocket();
var replacementCounter = 0;
var replacementSession = new HaivenSessionCore(
    replacementSocket,
    () => "same-token",
    () => $"replacement-{++replacementCounter}");
refusedChat.Attach(replacementSession);
replacementSession.Opened();
replacementSocket.Receive("""{"type":"auth.success","requestId":"replacement-1","success":true}""");
var replacementSends = replacementSocket.Sent.Where(f => JsonField(f, "type") == "chat.send").ToList();
Check("replacement resends with the stable client message id", replacementSends.Count == 1
    && JsonField(JsonField(replacementSends[0], "payload")!, "clientMessageId") == refusedPending.ClientMessageId);

var revokedSocket = new RecordingSocket();
var revokedSession = new HaivenSessionCore(revokedSocket, () => "revoked", () => "revoked-1");
var revokedActions = new List<HaivenCredentialAction>();
revokedSession.CredentialActionRequired += revokedActions.Add;
using (var playerAChat = new HaivenChat(() => "player-a-message"))
{
    playerAChat.Attach(revokedSession);
    revokedSession.Opened();
    revokedSocket.Receive("""{"type":"auth.success","requestId":"revoked-1","success":true}""");
    var playerAPending = playerAChat.Send("ch-1", "player A private content");
    revokedSocket.Finish(1008, "credential_revoked");
    Check("live revocation asks the host to clear", revokedActions.SequenceEqual(
        new[] { HaivenCredentialAction.Clear }));
    Check("clear closes player A's unanswered send as unconfirmed",
        playerAPending.State == OutgoingChatState.Unconfirmed);
}

var playerBSocket = new RecordingSocket();
var playerBSession = new HaivenSessionCore(playerBSocket, () => "player-b", () => "player-b-auth");
using (var playerBChat = new HaivenChat(() => "player-b-message"))
{
    playerBChat.Attach(playerBSession);
    playerBSession.Opened();
    playerBSocket.Receive("""{"type":"auth.success","requestId":"player-b-auth","success":true}""");
    var playerBSends = playerBSocket.Sent.Where(f => JsonField(f, "type") == "chat.send").ToList();
    Check("clear replaces the chat outbox before player B attaches", playerBSends.Count == 0);
}

// A game login is a host-owned handoff. The SDK creates and retains only the
// one-use PKCE transaction, then asks the host to open the first-party page.
var browser = new RecordingBrowser();
var transactions = new RecordingTransactions();
var tokenStore = new MemoryHandoffTokenStore();
var exchange = new RecordingExchange { AccessToken = "header.eyJleHAiOjF9.signature" };
var dispatcher = new RecordingDispatcher();
var clock = new FixedClock(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
tokenStore.Store(new HandoffCredential("discarded", "Bearer", clock.UtcNow.AddHours(1)));
tokenStore.Clear();
Check("the included handoff store erases a rejected credential", tokenStore.Credential is null);
var options = new HandoffOptions(
    new Uri("https://app.haiven.gg/connect/game?from=unity"),
    new Uri("https://api.haiven.gg/v0/auth/handoff/exchange"),
    "example-game",
    new Uri("haiven-example://auth/callback?mode=login"))
{
    ScreenHint = "signup",
};
var coordinator = new HandoffCoordinator(
    options, browser, transactions, tokenStore, exchange, dispatcher, clock);
var start = coordinator.BeginLogin();
var transaction = transactions.LastPut;
Check("begin stores the verifier transiently", transaction is not null);
Check("the PKCE verifier has 256 bits encoded without padding", transaction?.CodeVerifier.Length == 43 && !transaction.CodeVerifier.Contains('='));
Check("state has independent 256-bit entropy", transaction?.State.Length == 43 && transaction.State != transaction.CodeVerifier);
Check("the host receives the authorization URI", browser.Opened == start.AuthorizationUri);

var authorization = Query(start.AuthorizationUri);
Check("the authorization URI preserves its existing query", authorization["from"] == "unity");
Check("the authorization URI identifies the public integration", authorization["integration_id"] == "example-game");
Check("the authorization URI requests redirect completion", authorization["completion"] == "redirect");
Check("the authorization URI binds the exact redirect", authorization["redirect_uri"] == options.RedirectUri.AbsoluteUri);
Check("the authorization URI carries state", authorization["state"] == start.State);
Check("the authorization URI requires S256", authorization["code_challenge_method"] == "S256");
Check("the PKCE challenge is SHA-256 of the retained verifier", authorization["code_challenge"] == HandoffCoordinator.Sha256Base64Url(transaction?.CodeVerifier ?? ""));
Check("S256 matches the RFC 7636 vector",
    HandoffCoordinator.Sha256Base64Url("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
        == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
Check("optional signup intent crosses the browser boundary", authorization["screen_hint"] == "signup");

try
{
    await coordinator.CompleteAsync(new Uri(
        "haiven-example://other/callback?mode=login&code=attacker&state=" + start.State));
    Check("callbacks from another target are rejected", false);
}
catch (HandoffException)
{
    Check("callback target is validated before state is consumed", transactions.Contains(start.State));
}

try
{
    await coordinator.CompleteAsync(new Uri("haiven-example://auth/callback?mode=login&code=attacker&state=wrong"));
    Check("unknown state is rejected", false);
}
catch (HandoffException)
{
    Check("state is validated before exchange", exchange.Calls == 0);
    Check("a bad callback cannot destroy the real transaction", transactions.Contains(start.State));
}

var callback = new Uri("haiven-example://auth/callback?mode=login&code=one%20use%2Fcode&state=" + Uri.EscapeDataString(start.State));
var completedThread = -1;
coordinator.Completed += _ => completedThread = Environment.CurrentManagedThreadId;
var credential = await coordinator.CompleteAsync(callback);
Check("the one-use transaction is erased before exchange completes", !transactions.Contains(start.State));
Check("the handoff exchanges exactly once", exchange.Calls == 1);
Check("the exchange receives the retained verifier", exchange.Last?.CodeVerifier == transaction?.CodeVerifier);
Check("the exchange receives the exact integration", exchange.Last?.IntegrationId == "example-game");
Check("the exchange receives the exact redirect", exchange.Last?.RedirectUri == options.RedirectUri);
Check("the access token is host-owned after exchange", tokenStore.Credential == credential && credential.AccessToken == "header.eyJleHAiOjF9.signature");
Check("expiry is stored as an absolute instant", credential.ExpiresAt == clock.UtcNow.AddSeconds(300));
Check("JWT-shaped bearer claims do not override exchange metadata", credential.ExpiresAt != DateTimeOffset.FromUnixTimeSeconds(1));
Check("token storage and completion use the dispatcher", dispatcher.Calls == 1 && completedThread == dispatcher.ThreadId);
Check("the credential has no refresh-token surface", typeof(HandoffCredential).GetProperties().All(property => property.Name != "RefreshToken"));

foreach (var reason in Enum.GetValues<HandoffCredentialEndReason>())
{
    var restartsBefore = browser.OpenCalls;
    var restarted = coordinator.RestartLogin(reason);
    Check(reason + " clears the rejected credential", tokenStore.Credential is null);
    Check(reason + " starts a fresh handoff", browser.OpenCalls == restartsBefore + 1 && transactions.Contains(restarted.State));
}
try
{
    HandoffExchangeResponse.ParseJson(
        "{\"access_token\":\"access\",\"token_type\":\"Bearer\",\"expires_in\":300,\"refresh_token\":\"forbidden\"}");
    Check("a refresh credential is rejected", false);
}
catch (HandoffException)
{
    Check("a refresh credential is rejected", true);
}

var encoded = exchange.Last?.ToFormBody();
Check("the exchange body is WebGL-safe form data",
    encoded == "code=one%20use%2Fcode&code_verifier=" + transaction?.CodeVerifier
        + "&integration_id=example-game&redirect_uri=haiven-example%3A%2F%2Fauth%2Fcallback%3Fmode%3Dlogin");

var expiredStart = coordinator.BeginLogin();
clock.Advance(TimeSpan.FromMinutes(16));
try
{
    await coordinator.CompleteAsync(new Uri(
        "haiven-example://auth/callback?mode=login&code=late&state=" + expiredStart.State));
    Check("expired transactions are rejected", false);
}
catch (HandoffException)
{
    Check("expired verifiers are erased without exchange", !transactions.Contains(expiredStart.State) && exchange.Calls == 1);
}

var cancelledStart = coordinator.BeginLogin();
var cancelled = new CancellationToken(canceled: true);
try
{
    await coordinator.CompleteAsync(
        new Uri("haiven-example://auth/callback?mode=login&code=cancelled&state=" + cancelledStart.State),
        cancelled);
    Check("cancellation is observed", false);
}
catch (OperationCanceledException)
{
    Check("cancellation clears the verifier", !transactions.Contains(cancelledStart.State));
}

// ADR 0041: a durable credential arrives with a null expires_in, and a store
// that keeps it lets the next launch reuse it instead of opening the browser.
Check("a null expires_in parses as a durable credential",
    HandoffExchangeResponse.ParseJson("{\"access_token\":\"durable\",\"token_type\":\"Bearer\",\"expires_in\":null}").ExpiresInSeconds is null);
foreach (var malformed in new[]
{
    "{\"access_token\":\"a\",\"token_type\":\"Bearer\"}",
    "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":\"300\"}",
    "{\"access_token\":\"a\",\"token_type\":\"Bearer\",\"expires_in\":-1}",
})
{
    try
    {
        HandoffExchangeResponse.ParseJson(malformed);
        Check("a missing or malformed expires_in is still refused: " + malformed, false);
    }
    catch (HandoffException)
    {
        Check("a missing or malformed expires_in is still refused: " + malformed, true);
    }
}

var savedStore = new MemoryHandoffTokenStore();
var durableBrowser = new RecordingBrowser();
var durableCoordinator = new HandoffCoordinator(
    options, durableBrowser, new MemoryHandoffTransactionStore(), savedStore,
    new RecordingExchange { AccessToken = "durable-access", ExpiresIn = null }, new ImmediateHandoffDispatcher(), clock);
Check("nothing saved resumes nothing", durableCoordinator.Resume() is null);
var durableStart = durableCoordinator.BeginLogin();
var durableCredential = await durableCoordinator.CompleteAsync(new Uri(
    "haiven-example://auth/callback?mode=login&code=durable&state=" + Uri.EscapeDataString(durableStart.State)));
Check("a durable credential has no expiry", durableCredential.ExpiresAt is null && savedStore.Credential?.ExpiresAt is null);
Check("a durable credential never expires", !durableCredential.HasExpired(DateTimeOffset.MaxValue));

// A second coordinator over the same store stands in for the next launch.
var relaunchBrowser = new RecordingBrowser();
var relaunch = new HandoffCoordinator(
    options, relaunchBrowser, new MemoryHandoffTransactionStore(), savedStore,
    new RecordingExchange(), new ImmediateHandoffDispatcher(), clock);
HandoffCredential? relaunchCompleted = null;
relaunch.Completed += value => relaunchCompleted = value;
var resumed = relaunch.Resume();
Check("a saved credential is reused at start", resumed?.AccessToken == "durable-access");
Check("reuse opens no browser", relaunchBrowser.OpenCalls == 0);
Check("reuse raises Completed like a finished handoff", ReferenceEquals(relaunchCompleted, resumed));

foreach (var unusable in new[]
{
    new HandoffCredential("finite-access", "Bearer", clock.UtcNow),
    new HandoffCredential("", "Bearer", null),
})
{
    savedStore.Store(unusable);
    Check("an expired or empty saved credential is not reused", relaunch.Resume() is null);
    Check("an expired or empty saved credential is cleared", savedStore.Credential is null);
}
savedStore.Store(new HandoffCredential("finite-access", "Bearer", clock.UtcNow.AddMinutes(1)));
Check("an unexpired finite credential is reused", relaunch.Resume()?.AccessToken == "finite-access");
Check("a store that cannot load resumes nothing", new HandoffCoordinator(
    options, new RecordingBrowser(), new MemoryHandoffTransactionStore(), new StoreOnlyTokens(),
    new RecordingExchange(), new ImmediateHandoffDispatcher(), clock).Resume() is null);

// A desktop host without a custom scheme receives the redirect on a fixed
// loopback port. The listener hands the callback over unparsed, so the
// coordinator's exact-redirect and state checks still decide.
foreach (var notLoopback in new[]
{
    "http://localhost:47615/haiven/callback",
    "https://127.0.0.1:47615/haiven/callback",
    "http://127.0.0.1/haiven/callback",
    "http://10.0.0.1:47615/haiven/callback",
})
{
    try
    {
        LoopbackHandoffListener.LoopbackAddress(new Uri(notLoopback));
        Check("a non-loopback-literal redirect is refused: " + notLoopback, false);
    }
    catch (ArgumentException)
    {
        Check("a non-loopback-literal redirect is refused: " + notLoopback, true);
    }
}
Check("an IPv6 loopback literal is accepted",
    System.Net.IPAddress.IPv6Loopback.Equals(LoopbackHandoffListener.LoopbackAddress(new Uri("http://[::1]:47615/haiven/callback"))));

var loopbackPort = FreeLoopbackPort();
var loopbackRedirect = new Uri($"http://127.0.0.1:{loopbackPort}/haiven/callback?game=demo");
var loopbackStore = new MemoryHandoffTokenStore();
var loopbackExchange = new RecordingExchange();
var loopbackBrowser = new RecordingBrowser();
var loopbackCoordinator = new HandoffCoordinator(
    new HandoffOptions(
        new Uri("https://app.haiven.gg/connect/game"),
        new Uri("https://api.haiven.gg/v0/auth/handoff/exchange"),
        "example-game",
        loopbackRedirect),
    loopbackBrowser,
    new MemoryHandoffTransactionStore(),
    loopbackStore,
    loopbackExchange,
    new ImmediateHandoffDispatcher());
using (var listener = LoopbackHandoffListener.Start(loopbackRedirect))
{
    try
    {
        LoopbackHandoffListener.Start(loopbackRedirect).Dispose();
        Check("a second listener on a bound port fails the login", false);
    }
    catch (HandoffException)
    {
        Check("a second listener on a bound port fails the login", true);
    }

    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var login = loopbackCoordinator.LoginAsync(listener, timeout.Token);
    var opened = Query(loopbackBrowser.Opened!);
    using var http = new System.Net.Http.HttpClient();
    var probe = await http.GetAsync($"http://127.0.0.1:{loopbackPort}/favicon.ico");
    Check("another path is answered 404 without ending the wait",
        probe.StatusCode == System.Net.HttpStatusCode.NotFound && !login.IsCompleted);
    var page = await http.GetAsync(
        loopbackRedirect.AbsoluteUri + "&code=loop-code&state=" + Uri.EscapeDataString(opened["state"]));
    var pageText = await page.Content.ReadAsStringAsync();
    var loopbackCredential = await login;
    Check("the redirect path completes the login", loopbackCredential.AccessToken == "opaque-access");
    Check("the callback page is no-store and echoes nothing from the request",
        page.IsSuccessStatusCode
        && page.Headers.CacheControl?.NoStore == true
        && !pageText.Contains("loop-code", StringComparison.Ordinal)
        && !pageText.Contains(opened["state"], StringComparison.Ordinal));
    Check("the loopback callback exchanges once with the exact redirect",
        loopbackExchange.Calls == 1
        && loopbackExchange.Last?.Code == "loop-code"
        && loopbackExchange.Last.RedirectUri == loopbackRedirect);
    Check("the listener stops after one callback", !CanConnect(loopbackPort));
}

using (var listener = LoopbackHandoffListener.Start(loopbackRedirect))
{
    using var stop = new CancellationTokenSource();
    var waiting = listener.ReceiveAsync(stop.Token);
    stop.Cancel();
    try
    {
        await waiting;
        Check("cancelling the wait stops the listener", false);
    }
    catch (OperationCanceledException)
    {
        Check("cancelling the wait stops the listener", !CanConnect(loopbackPort));
    }
}

// The generated REST operations: the request each one hands the transport, and
// the decoding of what comes back. The transport is fake, so this measures the
// generated half alone -- path and query encoding, body encoding, and DTOs.
var rest = new RecordingRestTransport();
var api = new RestApi(rest);

rest.Response = """{"asOf":"2026-10-01T00:00:00Z","projectionVersion":7,"entitlements":[{"code":"founder","kind":"founder","state":"active","validFrom":"2026-01-01T00:00:00Z","validTo":null}]}""";
var entitlements = await api.GetUsersMeEntitlementsAsync();
Check("the entitlement read is a GET", rest.Last?.Method == "GET");
Check("of the account entitlements path", rest.Last?.Path == "/users/me/entitlements");
Check("without a body", rest.Last?.Body is null && rest.Last?.ContentType is null);
Check("and decodes the projection", entitlements?.ProjectionVersion == 7);
Check("with each grant's kind and state",
    entitlements?.Entitlements?.Single() is { Kind: "founder", State: "active", ValidTo: null });

rest.Response = null;
await api.GetChatChannelsChannelIdMessagesAsync(
    "chan/1 x", null, "thread", null, CancellationToken.None);
Check("the old cancellation position and parent argument remain compatible",
    rest.Last?.Path == "/chat/channels/chan%2F1%20x/messages?parentMessageId=thread");
await api.GetChatChannelsChannelIdMessagesAsync(
    "chan/1 x", beforeMessageId: "m&2", limit: 25, afterMessageId: "m-after");
Check("path parameters are percent-encoded",
    rest.Last?.Path == "/chat/channels/chan%2F1%20x/messages?beforeMessageId=m%262&afterMessageId=m-after&limit=25");

await api.GetAppsAsync(tag: new[] { "rpg", "co op" }, earlyAccess: true);
Check("array queries repeat and booleans are lower case",
    rest.Last?.Path == "/apps?tag=rpg&tag=co%20op&earlyAccess=true");

await api.PostAuthLoginAsync(new LoginRequest { Username = "player", Password = "pw" });
Check("JSON bodies are declared as JSON", rest.Last?.ContentType == "application/json");
Check("and omit unset optional members", rest.Last?.Body == """{"password":"pw","username":"player"}""");

rest.Response = """{"access_token":"opaque","expires_in":300,"token_type":"Bearer"}""";
var exchanged = await api.PostAuthHandoffExchangeAsync(new PostAuthHandoffExchangeBody
{
    Code = "c 1",
    CodeVerifier = "v",
    IntegrationId = "game",
    RedirectUri = "haiven-example://auth/callback",
});
Check("the handoff exchange is a form", rest.Last?.ContentType == "application/x-www-form-urlencoded");
Check("with every field form-encoded",
    rest.Last?.Body == "code=c%201&code_verifier=v&integration_id=game&redirect_uri=haiven-example%3A%2F%2Fauth%2Fcallback");
Check("and decodes the snake_case credential", exchanged?.AccessToken == "opaque" && exchanged.ExpiresIn == 300);

var sends = rest.Calls;
var sso = api.GetAuthSsoProviderUrl("google", "challenge", "S256");
Check("SSO initiation is a URL, not a request", rest.Calls == sends);
Check("resolved against the transport's base with its PKCE challenge", sso == new Uri("https://api.example.test/auth/sso/google?code_challenge=challenge&code_challenge_method=S256"));
Check("the callback is a URL too",
    api.GetAuthCallbackUrl("code", "st ate").AbsoluteUri == "https://api.example.test/auth/callback?code=code&state=st%20ate");

// The generated client contract types ship in Haiven.Sdk (and so Haiven.Sdk.Core,
// Unity, and Godot .NET). The property the emitter exists for has to survive
// into the package: an event has no `success`, and must not read as `false`.
var contractEvent = ContractsJson.ReadWsServerReplyV0(
    HaivenJson.Parse("""{"type":"chat.message","requestId":""}"""), "reply");
var contractRefusal = ContractsJson.ReadWsServerReplyV0(
    HaivenJson.Parse("""{"type":"chat.send","requestId":"rq-9","success":false,"error":"rate_limited"}"""), "reply");
Check("a shipped contract keeps an absent success absent", contractEvent?.Success is null);
Check("and a refusal a refusal", contractRefusal?.Success == false && contractRefusal.Error == "rate_limited");

// The generated WebSocket surface: typed requests over the shared session core,
// and an event router keyed by the wire types the AsyncAPI description binds.
var wsSocket = new RecordingSocket();
var wsCounter = 0;
var wsSession = new HaivenSessionCore(wsSocket, () => "token-ws", () => $"ws-{++wsCounter}");
var ws = new WsApi(wsSession);
wsSession.Opened();
wsSocket.Receive("""{"type":"auth.success","requestId":"","success":true}""");
var wsSent = ws.ChatSend(new Haiven.Sdk.Contracts.ChatSendRequestV0 { ChannelId = "c1", Content = "hi" });
Check("a generated request writes its wire type", JsonField(wsSocket.Sent[1], "type") == "chat.send");
Check("with the requestId it returns", JsonField(wsSocket.Sent[1], "requestId") == wsSent.RequestId);
Check("and a camelCase payload without absent members",
    JsonField(wsSocket.Sent[1], "payload") == """{"channelId":"c1","content":"hi"}""");
ws.TransportPing();
Check("ping goes out as ping, without a payload",
    JsonField(wsSocket.Sent[2], "type") == WsTypes.Ping && !wsSocket.Sent[2].Contains("\"payload\"", StringComparison.Ordinal));
var submission = WsApi.DecodeChatSendReply(ServerFrame.Parse(
    """{"type":"chat.send","requestId":"ws-2","success":true,"payload":{"moderationState":"accepted","message":{"id":"m1","channelId":"c1"}}}"""));
Check("a reply decodes into its contract type",
    submission?.ModerationState == "accepted" && submission.Message?.ChannelId == "c1");

var router = new WsEventRouter();
var linkedIdentities = new List<Haiven.Sdk.Contracts.AccountIdentityLinkedV0>();
var unsubscribeFailed = router.OnAccountIdentityLinkFailed(_ => { });
router.OnAccountIdentityLinked(linkedIdentities.Add);
Check("an OAuth identity-link event reaches its typed handler",
    router.Dispatch(ServerFrame.Parse("""{"type":"account.identity.linked","payload":{"provider":"google","email":"p@example.test"}}"""))
    && linkedIdentities.Count == 1 && linkedIdentities[0].Provider == "google" && linkedIdentities[0].Email == "p@example.test");
var projectionVersions = new List<long>();
router.OnEntitlementsUpdated(update => projectionVersions.Add(update.ProjectionVersion));
Check("the entitlement refresh hint reaches its typed handler",
    router.Dispatch(ServerFrame.Parse("""{"type":"entitlements.updated","payload":{"projectionVersion":5}}"""))
    && projectionVersions.Count == 1 && projectionVersions[0] == 5);
var motds = new List<Haiven.Sdk.Contracts.MotdV0>();
router.OnMotd(motds.Add);
Check("the Message of the Day reaches its typed handler",
    router.Dispatch(ServerFrame.Parse("""{"type":"motd","payload":{"id":"00000000-0000-4000-8000-000000000001","body":"New rewards are live.","startsAt":"2026-10-03T00:00:00Z"}}"""))
    && motds.Count == 1 && motds[0].Body == "New rewards are live.");
Check("a reply is never routed as an event",
    !router.Dispatch(ServerFrame.Parse("""{"type":"account.identity.linked","requestId":"","success":true}""")));
Check("an event type minted after this build stays unhandled",
    !router.Dispatch(ServerFrame.Parse("""{"type":"entitlements.minted.later","payload":{}}""")));
unsubscribeFailed();
Check("and so does an event after its handler unsubscribed",
    !router.Dispatch(ServerFrame.Parse("""{"type":"account.identity.link.failed","payload":{"provider":"apple","code":"identity_already_linked"}}""")));

// Headless chat: sends survive a lost socket under the same client message id.
{
    var chatIds = 0;
    var chat = new HaivenChat(() => $"cm-{++chatIds}");
    var changes = new List<(string Id, OutgoingChatState State)>();
    chat.OutgoingChanged += m => changes.Add((m.ClientMessageId, m.State));
    var received = new List<Haiven.Sdk.Contracts.ChatMessageV0>();
    chat.MessageReceived += received.Add;

    var detached = chat.Send("ch-1", "before any session");
    Check("a send with no session is kept, not thrown", detached.State == OutgoingChatState.Unconfirmed);

    var firstSocket = new RecordingSocket();
    var firstCounter = 0;
    var first = new HaivenSessionCore(firstSocket, () => "token", () => $"a-{++firstCounter}");
    chat.Attach(first);
    first.Opened();
    firstSocket.Receive("""{"type":"auth.success","requestId":"a-2","success":true}""");
    var resent = firstSocket.Sent.Where(f => JsonField(f, "type") == "chat.send").ToList();
    Check("attaching resends the unconfirmed message after auth", resent.Count == 1
        && JsonField(JsonField(resent[0], "payload")!, "clientMessageId") == "cm-1");

    firstSocket.Receive("""{"type":"chat.send","requestId":"a-1","success":true,"payload":{"message":{"id":"m-1","channelId":"ch-1","clientMessageId":"cm-1"},"moderationState":"allowed"}}""");
    Check("an allowed reply accepts the message", detached.State == OutgoingChatState.Accepted && detached.MessageId == "m-1");

    var held = chat.Send("ch-1", "held");
    firstSocket.Receive("""{"type":"chat.send","requestId":"a-3","success":true,"payload":{"message":{"id":"m-2","channelId":"ch-1"},"moderationState":"pending"}}""");
    Check("a pending reply holds the message", held.State == OutgoingChatState.Held);

    var lost = chat.Send("ch-1", "reply lost");
    firstSocket.Finish(1006, "network");
    Check("a send unanswered when the socket closes is unconfirmed", lost.State == OutgoingChatState.Unconfirmed);
    Check("and a finished one is not touched", detached.State == OutgoingChatState.Accepted);

    var secondSocket = new RecordingSocket();
    var secondCounter = 0;
    var second = new HaivenSessionCore(secondSocket, () => "token", () => $"b-{++secondCounter}");
    chat.Attach(second);
    second.Opened();
    secondSocket.Receive("""{"type":"auth.success","requestId":"b-2","success":true}""");
    var again = secondSocket.Sent.Where(f => JsonField(f, "type") == "chat.send").ToList();
    Check("the next session resends only the unconfirmed message, under its id", again.Count == 1
        && JsonField(JsonField(again[0], "payload")!, "clientMessageId") == lost.ClientMessageId);
    secondSocket.Receive("""{"type":"chat.send","requestId":"b-1","success":false,"error":"content_too_long"}""");
    Check("a refusal is final and keeps its code", lost.State == OutgoingChatState.Refused && lost.Error == "content_too_long");

    firstSocket.Receive("""{"type":"chat.message","payload":{"id":"m-9","channelId":"ch-1"}}""");
    Check("the detached session's events no longer arrive", received.Count == 0);
    secondSocket.Receive("""{"type":"chat.message","payload":{"id":"m-9","channelId":"ch-1","content":"hi"}}""");
    Check("a channel message reaches the game", received.Count == 1 && received[0].Content == "hi");

    Check("an unknown moderation state is treated as held, never as visible",
        HaivenChat.StateFor("quarantined") == OutgoingChatState.Held
        && HaivenChat.StateFor("hidden") == OutgoingChatState.Removed
        && HaivenChat.StateFor("warned") == OutgoingChatState.Accepted);
    chat.Forget(detached);
    Check("forgetting drops a finished message from the outbox", !chat.Outbox.Contains(detached));
}

// A bad server frame used to fault SystemNetSocket's fire-and-forget receive
// task. The fault was invisible, and every later acknowledgement was lost.
{
    using var liveSocket = new SystemNetSocket();
    var sessionIds = 0;
    using var liveSession = new HaivenSessionCore(
        liveSocket,
        () => "token",
        () => $"live-{++sessionIds}");
    using var liveChat = new HaivenChat(() => "live-message");
    liveChat.Attach(liveSession);
    var receiveFaults = new List<(string Payload, Exception Error)>();
    var receivedPayloads = new List<string>();
    liveSocket.ReceiveFaulted += (payload, error) => receiveFaults.Add((payload, error));
    liveSocket.Received += receivedPayloads.Add;

    liveSession.Opened();
    liveSocket.DispatchReceived("""{"type":"auth.success","requestId":"live-1","success":true}""");
    var liveMessage = liveChat.Send("ch-1", "keep receiving");
    liveSocket.DispatchReceived("{not json");
    liveSocket.DispatchReceived("""{"type":"chat.send","requestId":"live-2","success":true,"payload":{"message":{"id":"m-live","channelId":"ch-1","clientMessageId":"live-message"},"moderationState":"allowed"}}""");

    Check("a JSON callback fault is surfaced with its frame",
        receiveFaults.Count == 1
        && receiveFaults[0].Payload == "{not json"
        && receiveFaults[0].Error is HaivenJsonException);
    Check("one failing receive callback does not starve later subscribers",
        receivedPayloads.Contains("{not json"));
    Check("the receive path continues to the next server acknowledgement",
        liveMessage.State == OutgoingChatState.Accepted && liveMessage.MessageId == "m-live");
}

var (clientFailures, clientScenarios) = ClientConformance.Run();
failures.AddRange(clientFailures);
var (p2pFailures, p2pScenarios) = P2PConformance.Run();
failures.AddRange(p2pFailures);
await RewardsTests.RunAsync(Check);
await AnalyticsTests.RunAsync(Check);
await IdentityTests.RunAsync(Check);
await P2PSessionsTests.RunAsync(Check);
failures.AddRange(JsonCodecTests.Run());

// Quirk deliver-on-host-main: the Unity package's realtime socket delivers on
// the main thread by default, and only the explicit opt-out leaves events on the
// receive loop. The opt-out run is also the proof that the default's pass is not
// trivial: the same transport, unmarshalled, really is off the main thread.
var onMain = MainThreadDelivery.Run(() => new UnitySocket());
Check("the Unity realtime socket completes a receive and a close", onMain.Completed && onMain.Received == "ready" && onMain.CloseCode == 4000);
Check("a Unity realtime event is delivered on the host main thread", onMain.ReceivedThread == onMain.MainThread);
Check("a Unity realtime close is delivered on the host main thread", onMain.ClosedThread == onMain.MainThread);
var offMain = MainThreadDelivery.Run(() => new UnitySocket(HaivenDispatch.Direct));
Check("the opt-out completes a receive and a close", offMain.Completed && offMain.Received == "ready" && offMain.CloseCode == 4000);
Check("the opt-out delivers on the receive loop, not the main thread", offMain.ReceivedThread != offMain.MainThread && offMain.ReceivedThread != -1);
Check("the default Unity socket refuses to be created off the main thread", MainThreadDelivery.DefaultRefusesWithoutAMainContext());

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine($"     (JSON codec: {HaivenJson.Default.Name})");
Console.WriteLine("ok   C# JSON codec: reader, writer, generated contract and REST codecs");
Console.WriteLine("ok   C# envelope discriminant");
Console.WriteLine("ok   C# shared session core");
Console.WriteLine("ok   C# game auth handoff");
Console.WriteLine("ok   C# generated REST operations");
Console.WriteLine("ok   C# reward claims");
Console.WriteLine("ok   C# Unity analytics batcher");
Console.WriteLine("ok   C# identity and MOTD facade");
Console.WriteLine("ok   C# generated contract types");
Console.WriteLine("ok   C# generated WebSocket surface");
Console.WriteLine("ok   C# headless chat");
Console.WriteLine("ok   C# Unity realtime delivery on the host main thread");
Console.WriteLine($"ok   C# client conformance (gaming-client/v0): {clientScenarios} scenario(s)");
Console.WriteLine($"ok   C# P2P conformance (gaming-p2p-client/v0): {p2pScenarios} scenario(s)");
return 0;

static Dictionary<string, string> Query(Uri uri)
{
    return uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(field => field.Split(new[] { '=' }, 2))
        .ToDictionary(
            field => Uri.UnescapeDataString(field[0]),
            field => Uri.UnescapeDataString(field.Length == 1 ? "" : field[1]),
            StringComparer.Ordinal);
}

static int FreeLoopbackPort()
{
    var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    probe.Start();
    var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    return port;
}

static bool CanConnect(int port)
{
    try
    {
        using var client = new System.Net.Sockets.TcpClient();
        client.Connect(System.Net.IPAddress.Loopback, port);
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}

static string? JsonField(string json, string name)
{
    using var document = JsonDocument.Parse(json);
    if (!document.RootElement.TryGetProperty(name, out var value)) return null;
    return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
}

sealed class RecordingSocket : IHaivenSocket
{
    public List<string> Sent { get; } = new();
    public int CloseCalls { get; private set; }
    public event Action<string>? Received;
    public event Action<int, string>? Closed;
    public void Send(string payload) => Sent.Add(payload);
    public void Close() => CloseCalls++;
    public void Receive(string payload) => Received?.Invoke(payload);
    public void Finish(int code, string reason) => Closed?.Invoke(code, reason);
}

sealed class RecordingRestTransport : IRestApiTransport
{
    public RestRequest? Last { get; private set; }
    public int Calls { get; private set; }
    public string? Response { get; set; }

    public Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Last = request;
        return Task.FromResult(Response);
    }

    public Uri Url(string path) => new Uri("https://api.example.test" + path);
}

sealed class RecordingBrowser : IHandoffBrowser
{
    public Uri? Opened { get; private set; }
    public int OpenCalls { get; private set; }
    public void Open(Uri uri)
    {
        Opened = uri;
        OpenCalls++;
    }
}

sealed class RecordingTransactions : IHandoffTransactionStore
{
    private readonly Dictionary<string, HandoffTransaction> values = new(StringComparer.Ordinal);
    public HandoffTransaction? LastPut { get; private set; }
    public bool Contains(string state) => values.ContainsKey(state);
    public void Put(HandoffTransaction transaction)
    {
        LastPut = transaction;
        values[transaction.State] = transaction;
    }
    public HandoffTransaction? Take(string state)
    {
        if (!values.Remove(state, out var transaction)) return null;
        return transaction;
    }
    public void Remove(string state) => values.Remove(state);
}

sealed class RecordingExchange : IHandoffHttpTransport
{
    public string AccessToken { get; init; } = "opaque-access";
    public long? ExpiresIn { get; init; } = 300;
    public int Calls { get; private set; }
    public HandoffExchangeRequest? Last { get; private set; }
    public Task<HandoffExchangeResponse> ExchangeAsync(
        Uri exchangeUri,
        HandoffExchangeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        Last = request;
        return Task.FromResult(new HandoffExchangeResponse(AccessToken, "Bearer", ExpiresIn));
    }
}

/// <summary>A pre-ADR 0041 host store: it saves and clears but cannot load.</summary>
sealed class StoreOnlyTokens : IHandoffTokenStore
{
    public void Store(HandoffCredential credential) { }
    public void Clear() { }
}

sealed class RecordingDispatcher : IHandoffDispatcher
{
    public int Calls { get; private set; }
    public int ThreadId { get; private set; }
    public Task RunAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        ThreadId = Environment.CurrentManagedThreadId;
        action();
        return Task.CompletedTask;
    }
}

sealed class FixedClock : IHandoffClock
{
    public FixedClock(DateTimeOffset now) => UtcNow = now;
    public DateTimeOffset UtcNow { get; private set; }
    public void Advance(TimeSpan duration) => UtcNow += duration;
}
