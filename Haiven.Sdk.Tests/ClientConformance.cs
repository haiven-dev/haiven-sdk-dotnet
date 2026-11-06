// Runner #2 of the client corpus: HaivenSessionCore against a scripted server.
//
// The suite is gaming-client/v0 (conformance/gaming-client/v0 in this repository),
// the same file the TypeScript, C++ and GDScript runners execute.
// A scenario scripts exactly the frames the server sends; this drives the core
// through RecordingSocket and asserts what it wrote and what it handed up.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haiven.Sdk;
using Haiven.Sdk.Json;

static class ClientConformance
{
    /// <summary>The names this runner answers to in a scenario's <c>waivers</c>.</summary>
    private static readonly string[] Names = { "dotnet", "gaming/unity" };

    /// <summary>Runs every scenario; returns the failures and how many ran.</summary>
    public static (List<string> Failures, int Ran) Run()
    {
        var failures = new List<string>();
        var path = Path.Combine(
            RepoRoot(), "conformance", "gaming-client", "v0", "generated", "suite.json");
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
            var failure = RunScenario(scenario);
            if (failure is not null) failures.Add($"client conformance {id}: {failure}");
        }

        return (failures, ran);
    }

    private static string? RunScenario(JsonObject scenario)
    {
        var socket = new RecordingSocket();
        var counter = 0;
        var session = new HaivenSessionCore(socket, () => "scripted-token", () => $"rq-{++counter}");
        var delivered = new List<ServerFrame>();
        session.FrameReceived += received => delivered.Add(received.Frame);
        var bound = new Dictionary<string, string>();
        var read = 0;
        var seen = 0;
        var steps = scenario["steps"]!.AsArray();

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index]!.AsObject();
            var verb = (string)step["do"]!;
            var at = $"step {index + 1} ({verb})";
            switch (verb)
            {
                case "open":
                    session.Opened();
                    break;
                case "call":
                    session.Send(
                        (string)step["type"]!,
                        step["payload"] is { } payload ? HaivenJson.Parse(payload.ToJsonString()) : null);
                    break;
                case "serve":
                    socket.Receive(Resolve(step["frame"]!, bound)!.ToJsonString());
                    break;
                case "expectSent":
                {
                    if (read >= socket.Sent.Count) return $"{at}: expected a {(string)step["type"]!} and nothing was written";
                    var frame = JsonNode.Parse(socket.Sent[read++])!.AsObject();
                    if ((string)frame["type"]! != (string)step["type"]!) return $"{at}: wrote {(string)frame["type"]!}";
                    var requestId = (string)frame["requestId"]!;
                    if (step["bind"] is { } bind) bound[(string)bind!] = requestId;
                    if (step["same"] is { } same && bound.GetValueOrDefault((string)same!) != requestId)
                    {
                        return $"{at}: wrote {requestId}, not {(string)same!}";
                    }
                    break;
                }
                case "expectQuiet":
                    if (read < socket.Sent.Count) return $"{at}: also wrote {socket.Sent[read]}";
                    break;
                case "expectState":
                {
                    var state = session.State.ToString().ToLowerInvariant();
                    if (state != (string)step["state"]!) return $"{at}: is {state}";
                    break;
                }
                case "expectDelivered":
                {
                    var type = (string)step["type"]!;
                    var offset = delivered.Skip(seen).ToList().FindIndex(frame => frame.Type == type);
                    if (offset < 0) return $"{at}: no {type} was handed to the caller";
                    var frame = delivered[seen + offset];
                    seen += offset + 1;
                    if (step["error"] is { } error && frame.Error != (string)error!) return $"{at}: error {frame.Error}";
                    break;
                }
                default:
                    return $"{at}: unknown step";
            }
        }

        return null;
    }

    /// <summary>Replaces every <c>{"ref": alias}</c> with the request id bound to that alias.</summary>
    private static JsonNode? Resolve(JsonNode? node, IReadOnlyDictionary<string, string> bound)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count == 1 && obj["ref"] is JsonValue alias:
                return JsonValue.Create(bound[(string)alias!]);
            case JsonObject obj:
            {
                var copy = new JsonObject();
                foreach (var (key, value) in obj) copy[key] = Resolve(value, bound);
                return copy;
            }
            case JsonArray array:
                return new JsonArray(array.Select(item => Resolve(item, bound)).ToArray());
            default:
                return node?.DeepClone();
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
