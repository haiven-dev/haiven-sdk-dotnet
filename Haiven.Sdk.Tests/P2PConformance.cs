// The .NET runner of gaming-p2p-client/v0: HaivenP2PSession against a scripted
// game socket and REST API, with a fake peer transport and a manual clock.
//
// The suite is conformance/gaming-p2p-client/v0. A scenario scripts every frame
// and REST reply the server sends; this drives the controller and its signaling
// on one thread, as a game's main loop does, and asserts what they wrote, what
// they asked of the REST API, what they handed the peer transport and what they
// reported.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk;
using Haiven.Sdk.Rest;
using Haiven.Sdk.Tests;

static class P2PConformance
{
    /// <summary>The names this runner answers to in a scenario's <c>waivers</c>.</summary>
    private static readonly string[] Names = { "dotnet", "gaming/unity" };

    /// <summary>Runs every scenario; returns the failures and how many ran.</summary>
    public static (List<string> Failures, int Ran) Run()
    {
        var failures = new List<string>();
        var path = Path.Combine(RepoRoot(), "conformance", "gaming-p2p-client", "v0", "generated", "suite.json");
        var suite = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var claimed = Names.Append((string)suite["lifecycle"]!).ToArray();
        var ran = 0;

        foreach (var scenario in suite["scenarios"]!.AsArray().Select(node => node!.AsObject()))
        {
            var id = (string)scenario["id"]!;
            var waivers = scenario["waivers"]!.AsObject();
            if (claimed.FirstOrDefault(waivers.ContainsKey) is { } name)
            {
                Console.Error.WriteLine($"waived {id}: {(string)waivers[name]!}");
                continue;
            }

            ran++;
            string? failure = null;
            P2PSessionsTests.Loop.Run(async () => failure = await new Scenario(scenario).RunAsync());
            if (failure is not null) failures.Add($"p2p conformance {id}: {failure}");
        }

        return (failures, ran);
    }

    private sealed class Scenario
    {
        private readonly JsonObject scenario;
        private readonly Dictionary<string, string> bound = new Dictionary<string, string>();
        private readonly List<ScriptedSocket> sockets = new List<ScriptedSocket>();
        private readonly List<(RestRequest Request, TaskCompletionSource<string?> Reply)> rest = new List<(RestRequest, TaskCompletionSource<string?>)>();
        private readonly List<(string From, PeerSignal Signal)> signals = new List<(string, PeerSignal)>();
        private readonly List<(string Name, JsonObject Fields)> events = new List<(string, JsonObject)>();
        private readonly Roster peers;
        private readonly HaivenP2PSession controller;
        private HaivenSessionSignaling? signaling;
        private double now;
        private int requests;
        private int sent;
        private int asked;
        private int answered;
        private int handed;
        private int seen;

        public Scenario(JsonObject scenario)
        {
            this.scenario = scenario;
            peers = new Roster(this);
            controller = new HaivenP2PSession(new HaivenSessionsClient(new ScriptedRest(this), 7), () => now) { Peers = peers };
            controller.Ended += (reason, code) => Report("ended", ("reason", Snake(reason.ToString())), ("code", code));
            controller.MemberLeft += (userId, reason) => Report("memberLeft", ("userId", userId), ("reason", reason));
            controller.MemberJoined += userId => Report("memberJoined", ("userId", userId));
            controller.ConnectionLost += () => Report("connectionLost");
            controller.Resumed += () => Report("resumed");
            controller.IceOptionsChanged += options => Report("iceOptions", ("policy", Policy(options)), ("servers", options.IceServers.Count));
        }

        private ScriptedSocket? Socket => sockets.LastOrDefault();

        public async Task<string?> RunAsync()
        {
            var steps = scenario["steps"]!.AsArray();
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index]!.AsObject();
                var verb = (string)step["do"]!;
                var failure = Do(verb, step);
                await Settle();
                if (failure != null) return $"step {index + 1} ({verb}): {failure}";
            }

            return null;
        }

        private string? Do(string verb, JsonObject step)
        {
            switch (verb)
            {
                case "attach":
                {
                    var socket = new ScriptedSocket();
                    socket.Core = new HaivenSessionCore(socket, () => "scripted-token", () => $"rq-{++requests}");
                    sockets.Add(socket);
                    sent = 0;
                    controller.Attach(socket.Core);
                    return null;
                }
                case "open":
                    Socket!.Core!.Opened();
                    return null;
                case "close":
                    Socket!.CloseWith((int)step["code"]!, (string?)step["reason"] ?? "");
                    return null;
                case "call":
                    return Call(step);
                case "serve":
                    Socket!.Deliver(Resolve(step["frame"]!)!.ToJsonString());
                    return null;
                case "expectSent":
                {
                    var socket = Socket!;
                    if (sent >= socket.Sent.Count) return $"expected a {(string)step["type"]!} and nothing was written";
                    var frame = JsonNode.Parse(socket.Sent[sent++])!.AsObject();
                    if ((string?)frame["type"] != (string)step["type"]!) return $"wrote {frame.ToJsonString()}";
                    if (step["bind"] is { } bind) bound[(string)bind!] = (string)frame["requestId"]!;
                    return Match(frame, step);
                }
                case "serveRest":
                {
                    if (answered >= rest.Count) return "no REST request is waiting for a reply";
                    var reply = rest[answered++].Reply;
                    if (step["lost"] is { } lost && (bool)lost!)
                    {
                        reply.SetException(new HttpRequestException("the connection closed before the reply"));
                        return null;
                    }
                    var status = (int)step["status"]!;
                    var body = step["body"]?.ToJsonString();
                    if (status >= 400) reply.SetException(new HaivenRestException(status, body));
                    else reply.SetResult(body);
                    return null;
                }
                case "expectRest":
                {
                    if (asked >= rest.Count) return $"expected {(string)step["method"]!} {(string)step["path"]!} and nothing was asked";
                    var request = rest[asked++].Request;
                    var call = request.Method + " " + request.Path;
                    if (call != (string)step["method"]! + " " + (string)step["path"]!) return $"asked {call}";
                    var body = request.Body == null ? new JsonObject() : JsonNode.Parse(request.Body)!.AsObject();
                    return Match(body, step);
                }
                case "advance":
                    now += (double)step["seconds"]!;
                    return null;
                case "signal":
                {
                    if (signaling == null) return "the controller has no signaling: it is not in a session";
                    var kind = (string)step["kind"]!;
                    var sdp = step["sdpRepeat"] is { } repeat
                        ? string.Concat(Enumerable.Repeat((string)repeat!, (int)step["times"]!))
                        : (string?)step["sdp"] ?? "";
                    signaling.Send((string)step["to"]!, kind switch
                    {
                        "offer" => PeerSignal.Offer(sdp),
                        "answer" => PeerSignal.Answer(sdp),
                        _ => PeerSignal.ForCandidate(new PeerIceCandidate((string)step["candidate"]!, "0", 0)),
                    });
                    return null;
                }
                case "expectSignal":
                {
                    if (handed >= signals.Count) return $"expected a {(string)step["kind"]!} and the transport got nothing";
                    var (from, signal) = signals[handed++];
                    var kind = signal.Kind.ToString().ToLowerInvariant();
                    if (from != (string)step["from"]! || kind != (string)step["kind"]!) return $"the transport got a {kind} from {from}";
                    if (step["sdp"] is { } sdp && signal.Sdp != (string)sdp!) return $"the sdp is {signal.Sdp}";
                    if (step["candidate"] is { } candidate && signal.Candidate?.Candidate != (string)candidate!)
                        return $"the candidate is {signal.Candidate?.Candidate}";
                    return null;
                }
                case "expectEvent":
                {
                    var name = (string)step["event"]!;
                    var offset = events.Skip(seen).ToList().FindIndex(e => e.Name == name);
                    if (offset < 0) return $"no {name} was reported; reported {string.Join(", ", events.Skip(seen).Select(e => e.Name))}";
                    var fields = events[seen + offset].Fields;
                    seen += offset + 1;
                    return Match(fields, step);
                }
                case "expectState":
                {
                    var state = Snake(controller.State.ToString());
                    return state == (string)step["state"]! ? null : $"is {state}";
                }
                case "expectQuiet":
                    if (Socket != null && sent < Socket.Sent.Count) return $"also wrote {Socket.Sent[sent]}";
                    if (asked < rest.Count) return $"also asked {rest[asked].Request.Method} {rest[asked].Request.Path}";
                    if (handed < signals.Count) return $"the transport also got a {signals[handed].Signal.Kind}";
                    return null;
                default:
                    return "unknown step";
            }
        }

        private string? Call(JsonObject step)
        {
            switch ((string)step["call"]!)
            {
                case "create":
                    Admit(() => controller.CreateAsync(new HaivenSessionCreateOptions((int)step["maxMembers"]!)));
                    return null;
                case "join":
                    var sessionId = (string)step["sessionId"]!;
                    var joinCode = (string?)step["joinCode"];
                    Admit(() => controller.JoinAsync(sessionId, joinCode));
                    return null;
                case "leave":
                    _ = controller.LeaveAsync();
                    return null;
                case "peerOpen":
                    peers.Open((string)step["userId"]!);
                    controller.ReportPeerConnected((string)step["userId"]!);
                    return null;
                default:
                    return "unknown call";
            }
        }

        private void Admit(Func<Task<HaivenSessionAdmission>> start)
        {
            async Task Await()
            {
                try
                {
                    var room = await start();
                    Report("admitted", ("sessionId", room.SessionId), ("participationId", room.ParticipationId),
                        ("hostUserId", room.HostUserId), ("hosting", room.Hosting),
                        ("icePolicy", controller.IceOptions == null ? null : Policy(controller.IceOptions)));
                }
                catch (Exception error)
                {
                    Report("refused", ("error", error switch
                    {
                        HaivenRestException rest => rest.Code ?? rest.StatusCode.ToString(CultureInfo.InvariantCulture),
                        HttpRequestException => "no_reply",
                        InvalidOperationException => "invalid_state",
                        _ => error.GetType().Name,
                    }));
                }
            }

            _ = Await();
        }

        /// <summary>
        /// Delivers what is queued and runs the controller and its signaling
        /// until nothing moves. A REST reply completes its waiter through the
        /// thread pool before the await returns to this loop, so the last rounds
        /// wait for that hop.
        /// </summary>
        private async Task Settle()
        {
            for (var round = 0; round < 40; round++)
            {
                if (round >= 30) await Task.Delay(2);
                controller.Tick();
                if (controller.Signaling != signaling)
                {
                    signaling = controller.Signaling;
                    if (signaling != null)
                    {
                        var current = signaling;
                        current.Received += (from, signal) => { if (signaling == current) signals.Add((from, signal)); };
                        current.Abandoned += (to, seq) => Report("abandoned", ("to", to), ("seq", seq));
                        current.Refused += (to, error) => Report("signalRefused", ("to", to), ("error", error));
                    }
                }
                signaling?.Poll();
                await Task.Yield();
            }
        }

        /// <summary>Checks <c>fields</c> against <paramref name="actual"/> and binds <c>capture</c>.</summary>
        private string? Match(JsonNode actual, JsonObject step)
        {
            if (step["fields"] is JsonObject fields)
            {
                foreach (var (path, expected) in fields)
                {
                    var value = Read(actual, path);
                    var failure = Compare(expected, value);
                    if (failure != null) return $"{path} {failure}";
                }
            }
            if (step["capture"] is JsonObject capture)
            {
                foreach (var (alias, path) in capture)
                {
                    var value = Read(actual, (string)path!);
                    if (value == null) return $"nothing at {(string)path!} to capture";
                    bound[alias] = value is JsonValue text && text.TryGetValue<string>(out var s) ? s : value.ToJsonString();
                }
            }
            return null;
        }

        private string? Compare(JsonNode? expected, JsonNode? actual)
        {
            var text = actual is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
            switch (expected)
            {
                case JsonObject { Count: 1 } ref_ when ref_["ref"] is JsonValue alias:
                    return text == bound[(string)alias!] ? null : $"is {Show(actual)}, not the bound {(string)alias!}";
                case JsonObject { Count: 1 } pattern when pattern["pattern"] is JsonValue regex:
                    return text != null && Regex.IsMatch(text, (string)regex!) ? null : $"is {Show(actual)}, not matching {(string)regex!}";
                case JsonObject { Count: 1 } limit when limit["maxSerializedBytes"] is JsonValue max:
                    return text != null && GoJsonBytes(text) <= (int)max! ? null : $"is {(text == null ? "not a string" : GoJsonBytes(text) + " bytes serialized")}";
                default:
                    return JsonNode.DeepEquals(expected, actual) ? null : $"is {Show(actual)}, not {Show(expected)}";
            }
        }

        private static string Show(JsonNode? node) => node?.ToJsonString() ?? "absent";

        /// <summary>The value at a dotted path; a step into a string reads the string as JSON.</summary>
        private static JsonNode? Read(JsonNode? node, string path)
        {
            foreach (var part in path.Split('.'))
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    try
                    {
                        node = JsonNode.Parse(text);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        return null;
                    }
                }
                node = node is JsonObject obj ? obj[part] : null;
            }
            return node;
        }

        /// <summary>
        /// Replaces every <c>{"ref": alias}</c> with the bound value and every
        /// <c>{"secondsFromNow": n}</c> with the UTC time n seconds from now.
        /// </summary>
        private JsonNode? Resolve(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj when obj.Count == 1 && obj["ref"] is JsonValue alias:
                    return JsonValue.Create(bound[(string)alias!]);
                case JsonObject obj when obj.Count == 1 && obj["secondsFromNow"] is JsonValue seconds:
                    return JsonValue.Create(DateTimeOffset.UtcNow.AddSeconds((double)seconds!).ToString("O", CultureInfo.InvariantCulture));
                case JsonObject obj:
                {
                    var copy = new JsonObject();
                    foreach (var (key, value) in obj) copy[key] = Resolve(value);
                    return copy;
                }
                case JsonArray array:
                    return new JsonArray(array.Select(Resolve).ToArray());
                default:
                    return node?.DeepClone();
            }
        }

        /// <summary>
        /// The UTF-8 size of a JSON string as Go's encoder writes it, quotes
        /// included: control characters, <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>,
        /// U+2028 and U+2029 take six bytes, and <c>"</c> and <c>\</c> two.
        /// </summary>
        private static int GoJsonBytes(string text)
        {
            var size = 2;
            foreach (var rune in text.EnumerateRunes())
            {
                var c = rune.Value;
                if (c == '"' || c == '\\' || c == '\n' || c == '\r' || c == '\t') size += 2;
                else if (c < 0x20 || c == '<' || c == '>' || c == '&' || c == 0x2028 || c == 0x2029) size += 6;
                else size += rune.Utf8SequenceLength;
            }
            return size;
        }

        private void Report(string name, params (string Key, object? Value)[] fields)
        {
            var obj = new JsonObject();
            foreach (var (key, value) in fields)
            {
                obj[key] = value switch
                {
                    null => null,
                    string s => JsonValue.Create(s),
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create(i),
                    _ => JsonValue.Create(value.ToString()),
                };
            }
            events.Add((name, obj));
        }

        private static string Policy(PeerConnectionOptions options) => options.Policy == PeerIcePolicy.Relay ? "relay" : "all";

        /// <summary>InSession becomes in_session.</summary>
        private static string Snake(string name) =>
            string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString()));

        private sealed class ScriptedSocket : IHaivenSocket
        {
            public HaivenSessionCore? Core { get; set; }

            public List<string> Sent { get; } = new List<string>();

            public bool IsClosed { get; private set; }

            public event Action<string>? Received;

            public event Action<int, string>? Closed;

            public void Send(string payload)
            {
                if (!IsClosed) Sent.Add(payload);
            }

            public void Close() => CloseWith(1000, "");

            public void CloseWith(int code, string reason)
            {
                if (IsClosed) return;
                IsClosed = true;
                Closed?.Invoke(code, reason);
            }

            public void Deliver(string payload)
            {
                if (!IsClosed) Received?.Invoke(payload);
            }
        }

        /// <summary>The REST API: each request waits until a serveRest step answers it.</summary>
        private sealed class ScriptedRest : IRestApiTransport
        {
            private readonly Scenario scenario;

            public ScriptedRest(Scenario scenario) => this.scenario = scenario;

            public Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
            {
                var reply = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                scenario.rest.Add((request, reply));
                return reply.Task;
            }

            public Uri Url(string path) => new Uri("https://api.example.test/v0" + path);
        }

        /// <summary>The peer transport's open connections, as the host drops through them.</summary>
        private sealed class Roster : IHaivenPeerRoster
        {
            private readonly Scenario scenario;
            private readonly List<string> open = new List<string>();

            public Roster(Scenario scenario) => this.scenario = scenario;

            public void Open(string userId)
            {
                if (!open.Contains(userId)) open.Add(userId);
            }

            public IEnumerable<string> ConnectedUsers() => open.ToList();

            public bool DropUser(string userId)
            {
                if (!open.Remove(userId)) return false;
                scenario.Report("peerDropped", ("userId", userId));
                return true;
            }
        }
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
