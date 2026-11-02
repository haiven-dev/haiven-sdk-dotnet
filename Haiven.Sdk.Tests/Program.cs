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

var failures = new List<string>();

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
Check("an event carries a payload", evt.Payload.HasValue);

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

var queued = session.Send("chat.send", new { body = "queued" });
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

// A game login is a host-owned handoff. The SDK creates and retains only the
// one-use PKCE transaction, then asks the host to open the first-party page.
var browser = new RecordingBrowser();
var transactions = new RecordingTransactions();
var tokenStore = new MemoryHandoffTokenStore();
var exchange = new RecordingExchange();
var dispatcher = new RecordingDispatcher();
var clock = new FixedClock(new DateTimeOffset(2026, 9, 17, 12, 0, 0, TimeSpan.Zero));
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
Check("the access token is host-owned after exchange", tokenStore.Credential == credential && credential.AccessToken == "opaque-access");
Check("expiry is stored as an absolute instant", credential.ExpiresAt == clock.UtcNow.AddSeconds(300));
Check("token storage and completion use the dispatcher", dispatcher.Calls == 1 && completedThread == dispatcher.ThreadId);
Check("the credential has no refresh-token surface", typeof(HandoffCredential).GetProperties().All(property => property.Name != "RefreshToken"));
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

if (failures.Count > 0)
{
    foreach (var failure in failures) Console.Error.WriteLine($"FAIL: {failure}");
    return 1;
}

Console.WriteLine("ok   C# envelope discriminant");
Console.WriteLine("ok   C# shared session core");
Console.WriteLine("ok   C# game auth handoff");
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

sealed class RecordingBrowser : IHandoffBrowser
{
    public Uri? Opened { get; private set; }
    public void Open(Uri uri) => Opened = uri;
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
        return Task.FromResult(new HandoffExchangeResponse("opaque-access", "Bearer", 300));
    }
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
