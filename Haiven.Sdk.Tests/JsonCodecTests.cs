// The JSON seam: the configured codec's reader and writer, and the generated
// contract and REST codecs over them. Runs under whichever codec Program.cs
// selected, so every assertion here is one both backends must satisfy; the few
// places they are allowed to differ (the reflective fallback) branch on it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Haiven.Sdk;
using Haiven.Sdk.Contracts;
using Haiven.Sdk.Json;
using Haiven.Sdk.Json.SystemTextJson;
using RestJson = Haiven.Sdk.Rest.RestJson;

static class JsonCodecTests
{
    public static List<string> Run()
    {
        var failures = new List<string>();
        void Check(string what, bool condition)
        {
            if (!condition) failures.Add($"[{HaivenJson.Default.Name}] {what}");
        }

        bool Throws<TException>(Action action)
            where TException : Exception
        {
            try
            {
                action();
                return false;
            }
            catch (TException)
            {
                return true;
            }
        }

        var codec = HaivenJson.Default;
        var reflective = codec is SystemTextJsonCodec;

        Check("Haiven.Sdk does not reference System.Text.Json",
            typeof(HaivenSessionCore).Assembly.GetReferencedAssemblies().All(name => name.Name != "System.Text.Json"));

        // --- The reader ---------------------------------------------------------
        var escaped = codec.Parse("""  {"s":"q\"b\\s\/ \b\f\n\r\t \u00e9 \ud83d\ude00","n":-0.5e-3,"a":[true,false,null],"o":{}}  """);
        Check("a string's escapes decode", escaped.TryGetProperty("s", out var text)
            && text.GetString() == "q\"b\\s/ \b\f\n\r\t \u00e9 \U0001F600");
        Check("a number keeps its source text", escaped.TryGetProperty("n", out var number)
            && number.GetNumberText() == "-0.5e-3");
        Check("an array keeps its kinds in order", escaped.TryGetProperty("a", out var array)
            && array.EnumerateArray().Select(item => item.Kind).SequenceEqual(
                new[] { HaivenJsonKind.True, HaivenJsonKind.False, HaivenJsonKind.Null }));
        Check("an empty object is an object", escaped.TryGetProperty("o", out var empty)
            && empty.Kind == HaivenJsonKind.Object && !empty.EnumerateObject().Any());

        var presence = codec.Parse("""{"present":null}""");
        Check("a member sent as null is present", presence.TryGetProperty("present", out var sentNull)
            && sentNull.Kind == HaivenJsonKind.Null);
        Check("a member not sent is absent", !presence.TryGetProperty("absent", out _));
        Check("a duplicate member resolves to the last", codec.Parse("""{"k":1,"k":2}""").TryGetProperty("k", out var duplicate)
            && duplicate.GetNumberText() == "2");

        var big = codec.Parse("""{"id":9007199254740993}""");
        Check("a 64-bit integer past 2^53 reads exactly", big.TryGetProperty("id", out var id)
            && HaivenJsonRead.Int64(id, "id") == 9007199254740993L);

        foreach (var malformed in new[]
        {
            "", "{", "{\"a\":1,}", "[1,]", "01", "1.", ".5", "+1", "-", "1e", "\"\\x\"", "\"unterminated",
            "\"tab\there\"", "tru", "nul", "{} {}", "NaN", "'single'", "{a:1}", "[1 2]", "\"\\u12\"",
        })
        {
            Check($"malformed JSON is refused: {malformed}", Throws<HaivenJsonException>(() => codec.Parse(malformed)));
        }

        Check("64 levels of nesting parse", Throws<HaivenJsonException>(() => codec.Parse(new string('[', 64) + new string(']', 64))) == false);
        Check("65 levels are refused", Throws<HaivenJsonException>(() => codec.Parse(new string('[', 65) + new string(']', 65))));

        // --- The writer ---------------------------------------------------------
        var awkward = "q\"b\\s\n\u0001\u2028 \U0001F600 <&>";
        var written = HaivenJson.Serialize(new Dictionary<string, object?>
        {
            ["text"] = awkward,
            ["max"] = long.MaxValue,
            ["tenth"] = 0.1,
            ["flag"] = true,
            ["nothing"] = null,
            ["list"] = new object?[] { 1, "two", null },
        });
        var reread = codec.Parse(written);
        Check("a written string reads back exactly", reread.TryGetProperty("text", out var back) && back.GetString() == awkward);
        Check("a written long is exact", reread.TryGetProperty("max", out var max) && HaivenJsonRead.Int64(max, "max") == long.MaxValue);
        Check("a written double round-trips", reread.TryGetProperty("tenth", out var tenth) && HaivenJsonRead.Double(tenth, "tenth") == 0.1);
        Check("an explicit null in a dictionary is written", reread.TryGetProperty("nothing", out var nothing) && nothing.Kind == HaivenJsonKind.Null);
        Check("a sequence is an array", reread.TryGetProperty("list", out var list) && list.EnumerateArray().Count() == 3);
        Check("JSON has no NaN", Throws<ArgumentException>(() => HaivenJson.Serialize(double.NaN)));

        object nested = "leaf";
        for (var depth = 0; depth < BuiltInJsonCodec.MaxDepth; depth++) nested = new object[] { nested };
        Check("the built-in writer accepts its maximum nesting depth",
            !Throws<HaivenJsonException>(() => HaivenJson.Serialize(nested, BuiltInJsonCodec.Instance)));
        nested = new object[] { nested };
        Check("the built-in writer rejects excessive nesting with a controlled error",
            Throws<HaivenJsonException>(() => HaivenJson.Serialize(nested, BuiltInJsonCodec.Instance)));

        var cycle = new Dictionary<string, object?>();
        cycle["self"] = cycle;
        Check("the built-in writer rejects cyclic containers with a controlled error",
            Throws<HaivenJsonException>(() => HaivenJson.Serialize(cycle, BuiltInJsonCodec.Instance)));

        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Check("numbers ignore the game's culture", HaivenJson.Serialize(1.5) == "1.5"
                && HaivenJsonRead.Double(codec.Parse("1.5"), "n") == 1.5);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }

        Check("a node written by the other codec copies through",
            codec.Parse(HaivenJson.Serialize(BuiltInJsonCodec.Instance.Parse("""{"a":[1,{"b":"c"}]}"""))).ToJson().Replace(" ", "") == """{"a":[1,{"b":"c"}]}""");

        // --- Generated contract codecs ------------------------------------------
        var message = ContractsJson.ReadChatMessageV0(codec.Parse(
            """{"id":"m1","channelId":"c1","content":null,"author":{"id":"u1","username":"u","displayName":"U","profileImageUrl":"p","avatarFrame":""},"addedLater":{"x":1}}"""),
            "payload");
        Check("a contract reads its members", message.Id == "m1" && message.ChannelId == "c1" && message.Author?.Username == "u");
        Check("a nullable member sent as null is null", message.Content is null);
        Check("an absent optional member stays unset", message.ParentMessageId is null);

        var submission = ContractsJson.ReadChatMessageSubmissionV0(codec.Parse(
            """{"message":{"id":"m2","channelId":"c1","author":{"id":"u2","username":"local","displayName":"Local Player","profileImageUrl":null}},"moderationState":"allowed"}"""),
            "payload");
        Check("a chat submission accepts the API's nullable author profile image",
            submission.Message?.Author?.ProfileImageUrl is null);
        Check("a chat submission accepts an omitted optional author avatar frame",
            submission.Message?.Author?.AvatarFrame is null);

        var missing = Catch(() => ContractsJson.ReadChatMessageV0(codec.Parse("""{"id":"m1"}"""), "payload"));
        Check("a missing required member is an error naming it",
            missing is HaivenJsonException && missing.Message.Contains("ChatMessageV0.channelId: missing", StringComparison.Ordinal));
        Check("a wrong kind is an error naming the member",
            Catch(() => ContractsJson.ReadChatMessageV0(codec.Parse("""{"id":7,"channelId":"c1"}"""), "payload")) is HaivenJsonException wrong
            && wrong.Message.Contains("ChatMessageV0.id", StringComparison.Ordinal));
        Check("null where the property cannot hold it is an error",
            Throws<HaivenJsonException>(() => ContractsJson.ReadChatMessageV0(codec.Parse("""{"id":null,"channelId":"c1"}"""), "payload")));
        Check("an int member refuses a fraction",
            Throws<HaivenJsonException>(() => ContractsJson.ReadEntitlementsUpdatedV0(codec.Parse("""{"projectionVersion":1.5}"""), "payload")));
        Check("and a value past 32 bits",
            Throws<HaivenJsonException>(() => ContractsJson.ReadEntitlementsUpdatedV0(codec.Parse("""{"projectionVersion":4294967296}"""), "payload")));

        var request = new ChatSendRequestV0 { ChannelId = "c1", Content = "hi", ClientMessageId = null };
        var requestJson = HaivenJson.Serialize(request);
        var requestBack = ContractsJson.ReadChatSendRequestV0(codec.Parse(requestJson), "payload");
        Check("a contract round-trips", requestBack.ChannelId == "c1" && requestBack.Content == "hi");
        Check("a null member is omitted, not written as null", !requestJson.Contains("clientMessageId", StringComparison.Ordinal));

        var reply = ContractsJson.ReadWsServerReplyV0(codec.Parse("""{"type":"pong","requestId":"r"}"""), "reply");
        Check("an absent presence discriminant stays absent", reply.Success is null);

        // --- WsApi.Decode<T>: named at run time --------------------------------
        var frame = ServerFrame.Parse("""{"type":"notifications.list","requestId":"r","success":true,"payload":[{"id":"n1","userId":"u","kind":"k","variety":"v","title":"t","body":"b","data":"{}","isRead":false,"createdAt":"c","updatedAt":"u"}]}""");
        Check("an array-rooted contract decodes by type", WsApi.Decode<List<NotificationListV0Item>>(frame)?.Single().Id == "n1");
        Check("and through its generated reader", WsApi.DecodeNotificationsListReply(frame)?.Single().IsRead == false);
        Check("a scalar decodes by type", WsApi.Decode<string>(ServerFrame.Parse("""{"type":"x","payload":"c1"}""")) == "c1");
        Check("the node itself decodes by type", WsApi.Decode<IHaivenJsonNode>(frame)?.Kind == HaivenJsonKind.Array);
        Check("a frame without a payload decodes to default", WsApi.Decode<ChatMessageV0>(ServerFrame.Parse("""{"type":"x"}""")) is null);

        var gamePayload = ServerFrame.Parse("""{"type":"game.state","payload":{"score":3,"name":"p"}}""");
        if (reflective)
        {
            Check("the System.Text.Json codec reads a game's own type reflectively",
                WsApi.Decode<GameState>(gamePayload) is { Score: 3, Name: "p" });
        }
        else
        {
            Check("the built-in codec refuses a type it would have to reflect over",
                Catch(() => WsApi.Decode<GameState>(gamePayload)) is NotSupportedException refusal
                && refusal.Message.Contains("Haiven.Sdk.SystemTextJson", StringComparison.Ordinal));
        }

        // --- What a session can send --------------------------------------------
        var sent = Send(new GameState { Score = 4, Name = "q" });
        if (reflective)
        {
            Check("the System.Text.Json codec sends a game's own type in camelCase",
                sent is string json && codec.Parse(json).TryGetProperty("payload", out var p) && p.TryGetProperty("score", out _));
        }
        else
        {
            Check("the built-in codec refuses to reflect over a game's own type", sent is NotSupportedException);
        }

        Check("a type that writes itself is sent under either codec",
            Send(new SelfWriting()) is string selfJson && selfJson.Contains("\"self\":true", StringComparison.Ordinal));
        Check("a parsed node is sent as it was parsed",
            Send(codec.Parse("""{"k":[1,2]}""")) is string nodeJson && nodeJson.Replace(" ", "").Contains("\"payload\":{\"k\":[1,2]}", StringComparison.Ordinal));

        var previousDefault = HaivenJson.Default;
        try
        {
            HaivenJson.Default = BuiltInJsonCodec.Instance;
            var explicitSocket = new RecordingSocket();
            var explicitSession = new HaivenSessionCore(
                explicitSocket, () => "t", () => "r", SystemTextJsonCodec.Instance);
            explicitSession.Opened();
            explicitSocket.Receive("""{"type":"auth.success","requestId":"r","success":true}""");
            new WsApi(explicitSession).NotificationsUpdate(new NotificationUpdateRequestV0
            {
                Id = "00000000-0000-4000-8000-000000000001",
                Title = "t",
                Body = "b",
                Data = new GameState { Score = 7, Name = "raw" },
            });
            Check("a generated raw member uses the session's explicit codec",
                SystemTextJsonCodec.Instance.Parse(explicitSocket.Sent.Last())
                    .TryGetProperty("payload", out var explicitPayload)
                && explicitPayload.TryGetProperty("data", out var explicitData)
                && explicitData.TryGetProperty("score", out var explicitScore)
                && HaivenJsonRead.Int32(explicitScore, "payload.data.score") == 7);

            GameState? decoded = null;
            explicitSession.EventReceived += received => decoded = WsApi.Decode<GameState>(received.Frame);
            explicitSocket.Receive("""{"type":"game.state","payload":{"score":9,"name":"received"}}""");
            Check("a received generic payload uses the session's explicit codec",
                decoded is { Score: 9, Name: "received" });
        }
        finally
        {
            HaivenJson.Default = previousDefault;
        }

        // --- Generated REST codecs ----------------------------------------------
        var challenge = RestJson.ReadChallengeInfo(codec.Parse("""{"name":"n","parameters":{"a":"1","b":"2"}}"""), "response");
        Check("a REST map reads", challenge.Parameters?["b"] == "2" && challenge.Session is null);
        var challengeJson = HaivenJson.Serialize(challenge);
        Check("and writes back", RestJson.ReadChallengeInfo(codec.Parse(challengeJson), "response").Parameters?.Count == 2);
        var restMessage = RestJson.ReadMessageResponse(codec.Parse("""{"id":"m","lastReplyTime":{"seconds":5}}"""), "response");
        Check("an unconstrained REST member stays a node", restMessage.LastReplyTime?.Kind == HaivenJsonKind.Object);
        Check("a REST long refuses a string",
            Throws<HaivenJsonException>(() => RestJson.ReadAccountEntitlementsResponse(codec.Parse("""{"projectionVersion":"9"}"""), "response")));
        Check("and reads a number",
            RestJson.ReadAccountEntitlementsResponse(codec.Parse("""{"projectionVersion":9}"""), "response").ProjectionVersion == 9);

        return failures;
    }

    private static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    /// <summary>What a session wrote for one payload: the frame text, or the exception.</summary>
    private static object Send(object payload)
    {
        var socket = new RecordingSocket();
        var session = new HaivenSessionCore(socket, () => "t", () => "r");
        session.Opened();
        socket.Receive("""{"type":"auth.success","requestId":"r","success":true}""");
        try
        {
            session.Send("game.state", payload);
            return socket.Sent.Last();
        }
        catch (Exception error)
        {
            return error;
        }
    }

    public sealed class GameState
    {
        public int Score { get; set; }
        public string? Name { get; set; }
    }

    private sealed class SelfWriting : IHaivenJsonWritable
    {
        public void WriteJson(IHaivenJsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("self");
            writer.WriteBoolean(true);
            writer.WriteEndObject();
        }
    }
}
