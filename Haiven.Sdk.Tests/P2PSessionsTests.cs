using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk.Tests
{
    // P2P sessions without WebRTC: the controller, acknowledged signaling and
    // the sessions client over a scripted game server and REST API. What this
    // pins: signals arrive once and in order through drops and duplicates, an
    // unacknowledged signal goes out again on a resumed socket, admission binds
    // to the socket, ICE servers are read with their policy and refresh time,
    // a dropped socket resumes with the participation's proof inside the
    // window and ends after it, the host drops members the server removed or
    // the roster no longer lists, and every way a session ends is reported once.
    // It all runs on one thread, as a game's main loop drives it.
    internal static class P2PSessionsTests
    {
        public static Task RunAsync(Action<string, bool> check)
        {
            Loop.Run(() => SignalsArriveOnceAndInOrder(check));
            Loop.Run(() => AnUnacknowledgedSignalIsRetriedAndThenAbandoned(check));
            Loop.Run(() => ASignalLostWithTheOldSocketGoesOutOnTheResumedOne(check));
            Loop.Run(() => AnOfferWaitsWhileTheSocketIsClosed(check));
            Loop.Run(() => AHostAndAGuestEnterTheSameSession(check));
            Loop.Run(() => AGuestResumesOnANewSocketWithinTheWindow(check));
            Loop.Run(() => AResumeIsRefusedOrTheWindowEnds(check));
            Loop.Run(() => TheHostDropsMembersTheServerRemoved(check));
            Loop.Run(() => TheSessionEndsOnceForEachReason(check));
            Loop.Run(() => IceServersAreRefreshedWhenTheServerSays(check));
            Loop.Run(() => ARefusedCredentialEndsTheSessionButAnUncheckedOneResumes(check));
            Loop.Run(() => ALostAdmissionReplyIsRepeatedWithTheSameAdmission(check));
            Loop.Run(() => SignalsTheServerRefusesAreNotRetriedAndRateLimitsCostNoRetry(check));
            Loop.Run(() => ALargeDescriptionIsSentInFragmentsAndAssembled(check));
            Loop.Run(() => TheHostAcceptsOnlyANewerEpoch(check));
            PureHelpers(check);
            return Task.CompletedTask;
        }

        private static async Task SignalsArriveOnceAndInOrder(Action<string, bool> check)
        {
            var server = new GameServer();
            var now = 0.0;
            var host = server.Connect("host");
            var guest = server.Connect("guest");
            await server.Settle();
            var hostSignaling = new HaivenSessionSignaling(host.Channel, GameServer.SessionId, "host", null, () => now);
            var guestSignaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => now);
            hostSignaling.Start(asHost: true);
            guestSignaling.Start(asHost: false);
            check("a second start of running signaling succeeds, as a transport adapter's does after the session's",
                hostSignaling.Start(asHost: true) && guestSignaling.Start(asHost: false));
            var atHost = new List<string>();
            var atGuest = new List<string>();
            hostSignaling.Received += (from, signal) => atHost.Add(from + ":" + Describe(signal));
            guestSignaling.Received += (from, signal) => atGuest.Add(from + ":" + Describe(signal));

            server.Duplicate = true;
            guestSignaling.Send("host", PeerSignal.Offer("offer-sdp"));
            guestSignaling.Send("host", PeerSignal.ForCandidate(new PeerIceCandidate("candidate:1", "0", 0)));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            hostSignaling.Send("guest", PeerSignal.Answer("answer-sdp"));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);

            check("the host gets the offer and the candidate once each, in order",
                atHost.SequenceEqual(new[] { "guest:Offer:offer-sdp", "guest:Candidate:candidate:1/0/0" }));
            check("the guest gets the answer once", atGuest.SequenceEqual(new[] { "host:Answer:answer-sdp" }));
            check("every signal was acknowledged", hostSignaling.PendingAcknowledgements == 0 && guestSignaling.PendingAcknowledgements == 0);
            check("the server set who sent each signal", server.Relayed.All(r => r.From == "guest" || r.From == "host"));
        }

        private static async Task AnUnacknowledgedSignalIsRetriedAndThenAbandoned(Action<string, bool> check)
        {
            var server = new GameServer();
            var now = 0.0;
            var guest = server.Connect("guest");
            await server.Settle();
            var signaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => now);
            signaling.Start(asHost: false);
            var abandoned = new List<int>();
            signaling.Abandoned += (_, seq) => abandoned.Add(seq);

            // Nobody is there to acknowledge: the host never connected.
            signaling.Send("host", PeerSignal.Offer("offer-sdp"));
            await server.Settle(signaling.Poll);
            var tries = new List<int> { server.SignalsFrom("guest") };
            foreach (var at in new[] { 1.0, 3.0, 7.0 })
            {
                now = at;
                await server.Settle(signaling.Poll);
                tries.Add(server.SignalsFrom("guest"));
            }
            check("a signal is sent again after 1, 2 and 4 seconds", tries.SequenceEqual(new[] { 1, 2, 3, 4 }));
            now = 20;
            await server.Settle(signaling.Poll);
            check("then it is abandoned and reported", abandoned.SequenceEqual(new[] { 1 }) && signaling.PendingAcknowledgements == 0);

            signaling.Send("gone", PeerSignal.Offer("offer-sdp"));
            await server.Settle(signaling.Poll);
            check("a member who left is not retried for", signaling.PendingAcknowledgements == 0);
        }

        private static async Task ASignalLostWithTheOldSocketGoesOutOnTheResumedOne(Action<string, bool> check)
        {
            var server = new GameServer();
            var now = 0.0;
            var host = server.Connect("host");
            var guest = server.Connect("guest");
            await server.Settle();
            var hostSignaling = new HaivenSessionSignaling(host.Channel, GameServer.SessionId, "host", null, () => now);
            var guestSignaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => now);
            hostSignaling.Start(asHost: true);
            guestSignaling.Start(asHost: false);
            var arrived = new List<string>();
            hostSignaling.Received += (_, signal) => arrived.Add(Describe(signal));

            server.DropSignals = 1;
            guestSignaling.Send("host", PeerSignal.Offer("offer-sdp"));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            check("the dropped offer did not arrive", arrived.Count == 0);

            var resumed = server.Connect("guest");
            await server.Settle();
            guestSignaling.Rebind(resumed.Channel);
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            check("the unacknowledged offer went out on the resumed socket at once", arrived.SequenceEqual(new[] { "Offer:offer-sdp" }));
            check("and its acknowledgement reached it", guestSignaling.PendingAcknowledgements == 0);
        }

        // A guest whose socket closes before the data channels open: the
        // session resumes it, so signaling neither reports the host unreachable
        // nor gives up on the offer while the socket is down.
        private static async Task AnOfferWaitsWhileTheSocketIsClosed(Action<string, bool> check)
        {
            var server = new GameServer();
            var now = 0.0;
            var host = server.Connect("host");
            var guest = server.Connect("guest");
            await server.Settle();
            var hostSignaling = new HaivenSessionSignaling(host.Channel, GameServer.SessionId, "host", null, () => now);
            var guestSignaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => now);
            hostSignaling.Start(asHost: true);
            guestSignaling.Start(asHost: false);
            var unreachable = 0;
            var abandoned = 0;
            ((IHaivenPeerSignalingLifecycle)guestSignaling).HostUnreachable += _ => unreachable++;
            guestSignaling.Abandoned += (_, _) => abandoned++;
            var arrived = new List<string>();
            hostSignaling.Received += (_, signal) => arrived.Add(Describe(signal));

            server.DropSignals = 1;
            guestSignaling.Send("host", PeerSignal.Offer("offer-sdp"));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            guest.Socket.Drop();
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            foreach (var at in new[] { 1.0, 3.0, 7.0, 20.0 })
            {
                now = at;
                await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            }
            check("a closed game socket is not an unreachable host", unreachable == 0);
            check("the offer is not abandoned while the socket is closed", abandoned == 0 && guestSignaling.PendingAcknowledgements == 1);

            var resumed = server.Connect("guest");
            await server.Settle();
            guestSignaling.Rebind(resumed.Channel);
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            check("the offer goes out on the resumed socket", arrived.SequenceEqual(new[] { "Offer:offer-sdp" }) && guestSignaling.PendingAcknowledgements == 0);
        }

        private static async Task AHostAndAGuestEnterTheSameSession(Action<string, bool> check)
        {
            var server = new GameServer { RelayOnly = true, RefreshAfter = DateTimeOffset.UtcNow.AddMinutes(8) };
            var (host, hostPlayer) = await server.Controller("host");
            var (guest, guestPlayer) = await server.Controller("guest");

            var created = await server.Drive(host.CreateAsync(new HaivenSessionCreateOptions(4)
            {
                Visibility = HaivenSessionVisibility.Private,
                AllowMixedAges = true,
                Metadata = new Dictionary<string, string> { ["name"] = "Room" },
            }), host, guest);
            var createBody = HaivenJson.Parse(server.Rest.Bodies["POST /games/7/sessions"]);
            check("create binds to the host's socket", Text(createBody, "connectionId") == hostPlayer.ConnectionId);
            check("and sends a 43-character resume token", Text(createBody, "resumeToken").Length == 43);
            check("create asks for the visibility and mixed ages", Text(createBody, "visibility") == "private" && createBody.TryGetProperty("allowMixedAges", out var mixed) && mixed.Kind == HaivenJsonKind.True);
            check("the host knows its own user id from the roster", created.HostUserId == "host" && created.Hosting);
            check("the host is in the session", host.State == HaivenP2PState.InSession && host.Room == created);
            check("relay-only is read from the ICE servers", host.IceOptions!.Policy == PeerIcePolicy.Relay && host.IceOptions.IceServers.Count == 1);
            check("the host's signaling has no host to reach", host.Signaling!.HostUserId == null);

            var joinedSeen = new List<string>();
            host.MemberJoined += joinedSeen.Add;
            var joined = await server.Drive(guest.JoinAsync(GameServer.SessionId, "ABC123"), host, guest);
            server.Event("host", "session.member.joined", $"{{\"sessionId\":\"{GameServer.SessionId}\",\"userId\":\"guest\",\"participationId\":\"p-guest\"}}");
            await server.Settle(host.Tick, guest.Tick);
            var joinBody = HaivenJson.Parse(server.Rest.Bodies["POST /games/7/sessions/s-1/members"]);
            check("join binds to the guest's socket and sends the code", Text(joinBody, "connectionId") == guestPlayer.ConnectionId && Text(joinBody, "joinCode") == "ABC123");
            check("the guest knows the host", joined.HostUserId == "host" && !joined.Hosting && guest.Signaling!.HostUserId == "host");
            check("the host hears the guest join", joinedSeen.SequenceEqual(new[] { "guest" }));

            host.ReportPeerConnected("guest");
            guest.ReportPeerConnected("host");
            await server.Settle(host.Tick, guest.Tick);
            check("only the host reports an open peer connection", server.PeerConnected.SequenceEqual(new[] { "host:guest" }));

            var refused = false;
            try
            {
                await server.Drive(guest.JoinAsync(GameServer.SessionId), host, guest);
            }
            catch (InvalidOperationException)
            {
                refused = true;
            }
            check("one controller is in one session at a time", refused);

            var unbound = new HaivenP2PSession(new HaivenSessionsClient(server.Rest, 7));
            var noSocket = false;
            try
            {
                await unbound.JoinAsync(GameServer.SessionId);
            }
            catch (InvalidOperationException)
            {
                noSocket = true;
            }
            check("admission needs an authenticated game socket (SDK-P2P-1)", noSocket);
        }

        private static async Task AGuestResumesOnANewSocketWithinTheWindow(Action<string, bool> check)
        {
            var server = new GameServer();
            var (host, _) = await server.Controller("host");
            var (guest, first) = await server.Controller("guest");
            await server.Drive(host.CreateAsync(new HaivenSessionCreateOptions(4)), host, guest);
            var admission = await server.Drive(guest.JoinAsync(GameServer.SessionId), host, guest);
            var lost = 0;
            var resumed = 0;
            guest.ConnectionLost += () => lost++;
            guest.Resumed += () => resumed++;

            first.Socket.Drop();
            await server.Settle(host.Tick, guest.Tick);
            check("a dropped socket starts resuming", guest.State == HaivenP2PState.Resuming && lost == 1);

            var second = server.Connect("guest");
            guest.Attach(second.Session);
            await server.Settle(host.Tick, guest.Tick);
            var request = server.Resumes.Single();
            check("the resume carries the participation and the old connection",
                Text(request, "participationId") == "p-guest" && Text(request, "previousConnectionId") == first.ConnectionId);
            check("and the token sent with the join", Text(request, "resumeToken") == Text(HaivenJson.Parse(server.Rest.Bodies["POST /games/7/sessions/s-1/members"]), "resumeToken"));
            check("the participation moved to the new socket", resumed == 1 && guest.State == HaivenP2PState.InSession && admission.ConnectionId == second.ConnectionId);

            // The host's socket drops too; its roster is read again right after the resume.
            var rostersBefore = server.Rest.Count("GET /games/7/sessions/s-1");
            server.Player("host").Socket.Drop();
            await server.Settle(host.Tick);
            host.Attach(server.Connect("host").Session);
            await server.Settle(host.Tick, guest.Tick);
            check("the host reads the roster at once after its resume (SDK-P2P-10)", server.Rest.Count("GET /games/7/sessions/s-1") == rostersBefore + 1);
        }

        private static async Task AResumeIsRefusedOrTheWindowEnds(Action<string, bool> check)
        {
            var server = new GameServer();
            var (guest, first) = await server.Controller("guest");
            await server.Drive(guest.JoinAsync(GameServer.SessionId), guest);
            var ended = new List<(HaivenP2PEndReason, string?)>();
            guest.Ended += (reason, code) => ended.Add((reason, code));

            server.RefuseResume = true;
            first.Socket.Drop();
            await server.Settle(guest.Tick);
            guest.Attach(server.Connect("guest").Session);
            await server.Settle(guest.Tick);
            check("a refused resume ends the session with the server's code",
                ended.SequenceEqual(new[] { (HaivenP2PEndReason.ResumeRefused, (string?)"session_not_found") }) && guest.State == HaivenP2PState.Idle);

            var late = new GameServer();
            var now = 0.0;
            var (lateGuest, latePlayer) = await late.Controller("guest", () => now);
            await late.Drive(lateGuest.JoinAsync(GameServer.SessionId), lateGuest);
            var lateEnded = new List<HaivenP2PEndReason>();
            lateGuest.Ended += (reason, _) => lateEnded.Add(reason);
            latePlayer.Socket.Drop();
            await late.Settle(lateGuest.Tick);
            now = 29;
            await late.Settle(lateGuest.Tick);
            check("the session waits through the window", lateEnded.Count == 0);
            now = 31;
            await late.Settle(lateGuest.Tick);
            check("and ends when no socket came back in 30 seconds", lateEnded.SequenceEqual(new[] { HaivenP2PEndReason.ResumeWindowElapsed }));
            check("without trying to resume", late.Resumes.Count == 0);
        }

        // SDK-P2P-6 with haiven-api's game-socket closes: 1013 auth_unavailable
        // keeps the credential, so it resumes like a drop; any 1008, or a REST
        // 401, means the credential or the socket is refused and the session ends.
        private static async Task ARefusedCredentialEndsTheSessionButAnUncheckedOneResumes(Action<string, bool> check)
        {
            var server = new GameServer();
            var (guest, first) = await server.Controller("guest");
            await server.Drive(guest.JoinAsync(GameServer.SessionId), guest);
            var ended = new List<(HaivenP2PEndReason, string?)>();
            var resumed = 0;
            guest.Ended += (reason, code) => ended.Add((reason, code));
            guest.Resumed += () => resumed++;

            first.Socket.CloseWith(1013, "auth_unavailable");
            await server.Settle(guest.Tick);
            check("1013 auth_unavailable resumes like a drop", guest.State == HaivenP2PState.Resuming && ended.Count == 0);
            var second = server.Connect("guest");
            guest.Attach(second.Session);
            await server.Settle(guest.Tick);
            check("and the participation moves to the next socket", resumed == 1 && guest.State == HaivenP2PState.InSession);

            second.Socket.CloseWith(1008, "credential_revoked");
            await server.Settle(guest.Tick);
            check("1008 credential_revoked ends the session as signed out",
                ended.SequenceEqual(new[] { (HaivenP2PEndReason.SignedOut, (string?)"credential_revoked") }) && guest.State == HaivenP2PState.Idle);
            check("without trying to resume", server.Resumes.Count == 1);

            // The replacement socket is refused in its handshake while resuming.
            var refused = new GameServer();
            var (late, latePlayer) = await refused.Controller("guest");
            await refused.Drive(late.JoinAsync(GameServer.SessionId), late);
            var lateEnded = new List<(HaivenP2PEndReason, string?)>();
            late.Ended += (reason, code) => lateEnded.Add((reason, code));
            latePlayer.Socket.Drop();
            await refused.Settle(late.Tick);
            var replacement = refused.Connect("guest");
            late.Attach(replacement.Session);
            replacement.Socket.CloseWith(1008, "unauthorized");
            await refused.Settle(late.Tick);
            check("a replacement socket closed with 1008 unauthorized ends a resuming session",
                lateEnded.SequenceEqual(new[] { (HaivenP2PEndReason.SignedOut, (string?)"unauthorized") }) && refused.Resumes.Count == 0);

            var policy = new GameServer();
            var (child, childPlayer) = await policy.Controller("guest");
            await policy.Drive(child.JoinAsync(GameServer.SessionId), child);
            var childEnded = new List<(HaivenP2PEndReason, string?)>();
            child.Ended += (reason, code) => childEnded.Add((reason, code));
            childPlayer.Socket.CloseWith(1008, "parental_consent_denied");
            await policy.Settle(child.Tick);
            check("any other 1008 ends the session as a refused socket",
                childEnded.SequenceEqual(new[] { (HaivenP2PEndReason.SocketRefused, (string?)"parental_consent_denied") }));

            var rest = new GameServer();
            var now = 0.0;
            var (host, _) = await rest.Controller("host", () => now);
            await rest.Drive(host.CreateAsync(new HaivenSessionCreateOptions(4)), host);
            host.Peers = new Peers("guest");
            var hostEnded = new List<(HaivenP2PEndReason, string?)>();
            host.Ended += (reason, code) => hostEnded.Add((reason, code));
            rest.Rest.RosterUnauthorized = true;
            now = 31;
            await rest.Settle(host.Tick);
            check("a REST 401 on the roster read ends the session as signed out",
                hostEnded.SequenceEqual(new[] { (HaivenP2PEndReason.SignedOut, (string?)"unauthorized") }));
        }

        // SDK-P2P-3 under SDK-REST-5: the SDK does not resend an admission on
        // its own, but the game's next identical call reuses the admission id
        // and token, so the server returns its first answer instead of a second seat.
        private static async Task ALostAdmissionReplyIsRepeatedWithTheSameAdmission(Action<string, bool> check)
        {
            var server = new GameServer();
            var (guest, _) = await server.Controller("guest");
            server.Rest.LoseNextReply = true;
            var lost = false;
            try
            {
                await server.Drive(guest.JoinAsync(GameServer.SessionId, "ABC123"), guest);
            }
            catch (System.Net.Http.HttpRequestException)
            {
                lost = true;
            }
            check("a lost join reply reaches the caller", lost && guest.State == HaivenP2PState.Idle);
            check("and the SDK does not resend it on its own", server.Rest.Admissions.Count == 1);
            var firstToken = Text(HaivenJson.Parse(server.Rest.Bodies["POST /games/7/sessions/s-1/members"]), "resumeToken");

            await server.Drive(guest.JoinAsync(GameServer.SessionId, "ABC123"), guest);
            var repeated = HaivenJson.Parse(server.Rest.Bodies["POST /games/7/sessions/s-1/members"]);
            check("the same join repeats the admission id", server.Rest.Admissions.Count == 2 && server.Rest.Admissions[0] == server.Rest.Admissions[1]);
            check("and the resume token", Text(repeated, "resumeToken") == firstToken);
            await server.Drive(guest.LeaveAsync(), guest);

            await server.Drive(guest.JoinAsync(GameServer.SessionId, "ABC123"), guest);
            check("an admission that was answered is not repeated", server.Rest.Admissions[2] != server.Rest.Admissions[1]);
            await server.Drive(guest.LeaveAsync(), guest);

            server.Rest.LoseNextReply = true;
            try
            {
                await server.Drive(guest.JoinAsync(GameServer.SessionId, "ABC123"), guest);
            }
            catch (System.Net.Http.HttpRequestException)
            {
            }
            await server.Drive(guest.JoinAsync(GameServer.SessionId, "OTHER1"), guest);
            check("a join with other arguments is a new admission", server.Rest.Admissions[4] != server.Rest.Admissions[3]);
        }

        private static async Task SignalsTheServerRefusesAreNotRetriedAndRateLimitsCostNoRetry(Action<string, bool> check)
        {
            var server = new GameServer();
            var now = 0.0;
            var host = server.Connect("host");
            var guest = server.Connect("guest");
            await server.Settle();
            var hostSignaling = new HaivenSessionSignaling(host.Channel, GameServer.SessionId, "host", null, () => now);
            var guestSignaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => now);
            hostSignaling.Start(asHost: true);
            guestSignaling.Start(asHost: false);
            var refused = new List<string>();
            var abandoned = 0;
            guestSignaling.Refused += (to, code) => refused.Add(to + ":" + code);
            guestSignaling.Abandoned += (_, _) => abandoned++;
            var arrived = new List<string>();
            hostSignaling.Received += (_, signal) => arrived.Add(Describe(signal));

            guestSignaling.Send("host", PeerSignal.Offer(new string('a', HaivenSessionSignaling.MaxDescriptionBytes + 1)));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            check("an offer above 32 KiB is refused before sending",
                refused.SequenceEqual(new[] { "host:sdp_too_large" }) && server.SignalsFrom("guest") == 0 && guestSignaling.PendingAcknowledgements == 0);
            check("the size counts JSON escapes as haiven-api does",
                HaivenSessionSignaling.SerializedSize("a\"<\u00e9") == 2 + 1 + 2 + 6 + 2);

            server.RefuseSignals.Enqueue("invalid_payload");
            guestSignaling.Send("host", PeerSignal.Offer("offer-sdp"));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            now = 10;
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            check("invalid_payload is reported once and not retried",
                refused.LastOrDefault() == "host:invalid_payload" && server.SignalsFrom("guest") == 1 && guestSignaling.PendingAcknowledgements == 0 && abandoned == 0);

            // Rate limited on the first copy and the first retry: neither counts,
            // so the offer still has all three retries and is delivered.
            server.RefuseSignals.Enqueue("rate_limited");
            server.RefuseSignals.Enqueue("rate_limited");
            server.RefuseSignals.Enqueue("rate_limited");
            server.RefuseSignals.Enqueue("rate_limited");
            server.RefuseSignals.Enqueue("rate_limited");
            guestSignaling.Send("host", PeerSignal.Offer("offer-sdp"));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            foreach (var at in new[] { 11.0, 12.0, 13.0, 14.0, 15.0, 16.0 })
            {
                now = at;
                await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            }
            check("five rate-limited copies spend no retry and the sixth is delivered",
                abandoned == 0 && arrived.SequenceEqual(new[] { "Offer:offer-sdp" }) && guestSignaling.PendingAcknowledgements == 0);
        }

        // SDK-P2P-8: haiven-api refuses data above 4,096 serialized bytes, so a
        // description is cut into parts that each fit, with escapes counted, and
        // the receiver hands on the whole SDP once.
        private static async Task ALargeDescriptionIsSentInFragmentsAndAssembled(Action<string, bool> check)
        {
            var server = new GameServer();
            var host = server.Connect("host");
            var guest = server.Connect("guest");
            await server.Settle();
            var hostSignaling = new HaivenSessionSignaling(host.Channel, GameServer.SessionId, "host", null, () => 0);
            var guestSignaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => 0, "p-guest");
            hostSignaling.Start(asHost: true);
            guestSignaling.Start(asHost: false);
            var arrived = new List<string>();
            hostSignaling.Received += (_, signal) => arrived.Add(Describe(signal));

            var sdp = string.Join("\r\n", Enumerable.Range(0, 300).Select(i => $"a=candidate:{i} 1 udp 2122260223 10.0.{i % 200}.1 {5000 + i} typ host \"<&>\" \u00e9")) + "\r\n";
            guestSignaling.Send("host", PeerSignal.Offer(sdp));
            guestSignaling.Send("host", PeerSignal.ForCandidate(new PeerIceCandidate("candidate:x", "0", 0)));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            var sizes = server.SignalData.Select(HaivenSessionSignaling.SerializedSize).ToList();
            check("a large offer goes out as several messages, each within 4096 serialized bytes",
                server.SignalData.Count > 2 && sizes.All(size => size <= HaivenSessionSignaling.MaxSignalData));
            check("the host receives the whole offer once, then the candidate",
                arrived.SequenceEqual(new[] { "Offer:" + sdp, "Candidate:candidate:x/0/0" }));
            check("every fragment was acknowledged", guestSignaling.PendingAcknowledgements == 0);
            var parts = server.SignalData.Take(server.SignalData.Count - 1).Select(data => HaivenJson.Parse(data)).ToList();
            check("the fragments are numbered part 1..n of n",
                parts.Select((node, i) => node.TryGetProperty("part", out var part) && part.GetNumberText() == (i + 1).ToString()
                    && node.TryGetProperty("parts", out var all) && all.GetNumberText() == parts.Count.ToString()).All(ok => ok));
        }

        // The host starts a new attempt only for a higher epoch of the same
        // participation, whichever of its messages arrives first; a delayed
        // offer of an older attempt is acknowledged and dropped.
        private static async Task TheHostAcceptsOnlyANewerEpoch(Action<string, bool> check)
        {
            var server = new GameServer();
            var host = server.Connect("host");
            var guest = server.Connect("guest");
            await server.Settle();
            var hostSignaling = new HaivenSessionSignaling(host.Channel, GameServer.SessionId, "host", null, () => 0);
            var guestSignaling = new HaivenSessionSignaling(guest.Channel, GameServer.SessionId, "guest", "host", () => 0, "p-guest-3");
            hostSignaling.Start(asHost: true);
            guestSignaling.Start(asHost: false);
            var atHost = new List<string>();
            var atGuest = new List<string>();
            hostSignaling.Received += (_, signal) => atHost.Add(Describe(signal));
            guestSignaling.Received += (_, signal) => atGuest.Add(Describe(signal));

            static string Data(PeerSignal signal, string participation, int epoch) =>
                HaivenSessionSignaling.Fragments(signal, participation, epoch)![0];
            void Deliver(string to, string from, string attemptId, int seq, string data) =>
                server.Event(to, "p2p.signal.received",
                    $"{{\"sessionId\":\"{GameServer.SessionId}\",\"fromUserId\":\"{from}\",\"attemptId\":\"{attemptId}\",\"seq\":{seq},\"data\":{HaivenJson.Serialize(data)}}}");

            var newer = Guid.NewGuid().ToString("D");
            var older = Guid.NewGuid().ToString("D");
            var candidate = PeerSignal.ForCandidate(new PeerIceCandidate("candidate:2", "0", 0));
            Deliver("host", "guest", newer, 2, Data(candidate, "p-guest", 2));
            Deliver("host", "guest", newer, 1, Data(PeerSignal.Offer("newer"), "p-guest", 2));
            await server.Settle(hostSignaling.Poll);
            check("a candidate that arrives before its offer waits for it", atHost.SequenceEqual(new[] { "Offer:newer", "Candidate:candidate:2/0/0" }));

            Deliver("host", "guest", older, 1, Data(PeerSignal.Offer("older"), "p-guest", 1));
            await server.Settle(hostSignaling.Poll);
            check("a delayed offer of a lower epoch does not replace the attempt", atHost.Count == 2);
            check("and is acknowledged all the same", server.Acks.Contains("host:" + older + ":1"));

            Deliver("host", "guest", Guid.NewGuid().ToString("D"), 1, Data(PeerSignal.Offer("rejoined"), "p-guest-2", 1));
            await server.Settle(hostSignaling.Poll);
            check("a new participation starts again at epoch 1", atHost.Last() == "Offer:rejoined");

            guestSignaling.Send("host", PeerSignal.Offer("first"));
            guestSignaling.Send("host", PeerSignal.Offer("second"));
            await server.Settle(hostSignaling.Poll, guestSignaling.Poll);
            var epochs = server.SignalData.Select(data => HaivenJson.Parse(data).TryGetProperty("epoch", out var e) ? e.GetNumberText() : "").ToList();
            check("each offer of a guest takes the next epoch", epochs.SequenceEqual(new[] { "1", "2" }));
            check("the host takes both, in order", atHost.Skip(3).SequenceEqual(new[] { "Offer:first", "Offer:second" }));

            Deliver("guest", "host", Guid.NewGuid().ToString("D"), 1, Data(PeerSignal.Answer("stale"), "p-guest", 1));
            await server.Settle(guestSignaling.Poll);
            check("a guest ignores an answer to an attempt it did not start", atGuest.Count == 0);
        }

        private static async Task TheHostDropsMembersTheServerRemoved(Action<string, bool> check)
        {
            var server = new GameServer();
            var now = 0.0;
            var (host, _) = await server.Controller("host", () => now);
            await server.Drive(host.CreateAsync(new HaivenSessionCreateOptions(8)), host);
            var peers = new Peers("a", "b", "c");
            host.Peers = peers;
            var left = new List<string>();
            host.MemberLeft += (userId, reason) => left.Add(userId + ":" + reason);

            server.Event("host", "session.member.left", $"{{\"sessionId\":\"{GameServer.SessionId}\",\"userId\":\"a\",\"participationId\":\"p-a\",\"reason\":\"left\"}}");
            await server.Settle(host.Tick);
            check("session.member.left closes that peer at once (SDK-P2P-11)", peers.Dropped.SequenceEqual(new[] { "a" }) && left.SequenceEqual(new[] { "a:left" }));

            server.Rest.Members = new[] { "host", "c" };
            now = 29;
            await server.Settle(host.Tick);
            check("the roster is not read before 30 seconds", !peers.Dropped.Contains("b"));
            now = 31;
            await server.Settle(host.Tick);
            check("the host drops a peer the roster no longer lists", peers.Dropped.SequenceEqual(new[] { "a", "b" }) && left.Last() == "b:not_on_roster");

            var ended = new List<HaivenP2PEndReason>();
            host.Ended += (reason, _) => ended.Add(reason);
            server.Rest.RosterGone = true;
            now = 62;
            await server.Settle(host.Tick);
            check("a roster read that finds the session gone ends it", ended.SequenceEqual(new[] { HaivenP2PEndReason.Closed }));
        }

        private static async Task TheSessionEndsOnceForEachReason(Action<string, bool> check)
        {
            var server = new GameServer();
            var (host, _) = await server.Controller("host");
            var (guest, _) = await server.Controller("guest");
            var hostEnded = new List<(HaivenP2PEndReason, string?)>();
            var guestEnded = new List<(HaivenP2PEndReason, string?)>();
            host.Ended += (reason, code) => hostEnded.Add((reason, code));
            guest.Ended += (reason, code) => guestEnded.Add((reason, code));

            await server.Drive(host.CreateAsync(new HaivenSessionCreateOptions(4)), host, guest);
            await server.Drive(guest.JoinAsync(GameServer.SessionId), host, guest);
            server.Event("guest", "session.member.left", $"{{\"sessionId\":\"{GameServer.SessionId}\",\"userId\":\"guest\",\"participationId\":\"p-guest\",\"reason\":\"kicked\"}}");
            await server.Settle(host.Tick, guest.Tick);
            check("member.left naming this participation removes it", guestEnded.SequenceEqual(new[] { (HaivenP2PEndReason.Removed, (string?)"kicked") }) && guest.Signaling == null);

            await server.Drive(guest.JoinAsync(GameServer.SessionId), host, guest);
            server.Event("guest", "session.closed", $"{{\"sessionId\":\"{GameServer.SessionId}\",\"reason\":\"host_left\"}}");
            server.Event("guest", "session.closed", $"{{\"sessionId\":\"{GameServer.SessionId}\",\"reason\":\"host_left\"}}");
            await server.Settle(host.Tick, guest.Tick);
            check("session.closed ends it once", guestEnded.Count == 2 && guestEnded[1] == (HaivenP2PEndReason.Closed, "host_left"));

            server.Event("host", "session.closed", "{\"sessionId\":\"another\",\"reason\":\"host_left\"}");
            await server.Settle(host.Tick);
            check("an event for another session is ignored", hostEnded.Count == 0);

            await server.Drive(host.LeaveAsync(), host);
            check("leave ends it and tells the server", hostEnded.SequenceEqual(new[] { (HaivenP2PEndReason.Left, (string?)null) })
                && server.Rest.Count("DELETE /games/7/sessions/s-1/members/me") == 1 && host.State == HaivenP2PState.Idle);
            await server.Drive(host.LeaveAsync(), host);
            check("a second leave does nothing", hostEnded.Count == 1 && server.Rest.Count("DELETE /games/7/sessions/s-1/members/me") == 1);
        }

        private static async Task IceServersAreRefreshedWhenTheServerSays(Action<string, bool> check)
        {
            var server = new GameServer { RefreshAfter = DateTimeOffset.UtcNow.AddSeconds(60) };
            var now = 0.0;
            var (host, _) = await server.Controller("host", () => now);
            await server.Drive(host.CreateAsync(new HaivenSessionCreateOptions(4)), host);
            var changed = new List<PeerConnectionOptions>();
            host.IceOptionsChanged += changed.Add;
            check("the first fetch is the one admission made", server.IceFetches == 1);

            now = 50;
            await server.Settle(host.Tick);
            check("nothing is fetched before refreshAfter", server.IceFetches == 1);
            server.FailIce = true;
            now = 61;
            await server.Settle(host.Tick);
            check("a failed fetch keeps the old servers", server.IceFetches == 2 && changed.Count == 0 && host.IceOptions != null);
            server.FailIce = false;
            now = 92;
            await server.Settle(host.Tick);
            check("and is tried again 30 seconds later (SDK-P2P-14)", server.IceFetches == 3 && changed.Count == 1);
        }

        private static void PureHelpers(Action<string, bool> check)
        {
            check("NotOnRoster lists connected peers the roster lacks",
                HaivenP2PSession.NotOnRoster(new[] { "a", "b", "c" }, new[] { "host", "a", "c" }).SequenceEqual(new[] { "b" }));
            check("NotOnRoster is empty when everyone is listed", HaivenP2PSession.NotOnRoster(new[] { "a" }, new[] { "a" }).Count == 0);
            foreach (var signal in new[]
            {
                PeerSignal.Offer("v=0\r\no=- 1 2 IN IP4 127.0.0.1\r\n"),
                PeerSignal.Answer("answer"),
                PeerSignal.ForCandidate(new PeerIceCandidate("candidate:2 1 udp 1 10.0.0.1 5000 typ host", null, null)),
                PeerSignal.ForCandidate(new PeerIceCandidate("candidate:3", "1", 1)),
            })
            {
                var data = HaivenSessionSignaling.Fragments(signal, "p-guest", 3);
                var node = data == null || data.Count != 1 ? null : HaivenJson.Parse(data[0]);
                check("a small " + signal.Kind + " is one message carrying the participation and epoch",
                    node != null && Text(node, "participationId") == "p-guest" && node.TryGetProperty("epoch", out var epoch) && epoch.GetNumberText() == "3");
            }
            check("a resume token is 32 bytes of unpadded base64url",
                Enumerable.Range(0, 20).Select(_ => HaivenSessionsClient.NewResumeToken()).All(t => t.Length == 43 && t.All(c => char.IsLetterOrDigit(c) || c == '-' || c == '_')));
        }

        private static string Describe(PeerSignal signal) => signal.Kind switch
        {
            PeerSignalKind.Candidate => "Candidate:" + signal.Candidate!.Candidate + "/" + signal.Candidate.SdpMid + "/" + signal.Candidate.SdpMLineIndex,
            _ => signal.Kind + ":" + signal.Sdp,
        };

        private static string Text(IHaivenJsonNode node, string name) =>
            node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.String ? value.GetString() : "";

        private sealed class Peers : IHaivenPeerRoster
        {
            private readonly List<string> connected;

            public Peers(params string[] users) => connected = users.ToList();

            public List<string> Dropped { get; } = new List<string>();

            public IEnumerable<string> ConnectedUsers() => connected.ToList();

            public bool DropUser(string userId)
            {
                if (!connected.Remove(userId)) return false;
                Dropped.Add(userId);
                return true;
            }
        }

        /// <summary>One game socket of one player, authenticated through the scripted server.</summary>
        private sealed class Player
        {
            public Player(GameServer server, string userId, string connectionId)
            {
                UserId = userId;
                ConnectionId = connectionId;
                Socket = new ScriptedSocket(server, this);
                Session = new HaivenSessionCore(Socket, () => "token-" + userId);
                Channel = new HaivenSessionChannel(Session);
                Session.Opened();
            }

            public string UserId { get; }

            public string ConnectionId { get; }

            public ScriptedSocket Socket { get; }

            public HaivenSessionCore Session { get; }

            public HaivenSessionChannel Channel { get; }
        }

        private sealed class ScriptedSocket : IHaivenSocket
        {
            private readonly GameServer server;
            private readonly Player player;

            public ScriptedSocket(GameServer server, Player player)
            {
                this.server = server;
                this.player = player;
            }

            public bool IsClosed { get; private set; }

            public event Action<string>? Received;

            public event Action<int, string>? Closed;

            public void Send(string payload)
            {
                if (!IsClosed) server.Handle(player, payload);
            }

            public void Close() => Drop();

            /// <summary>The connection is gone, as a lost network leaves it.</summary>
            public void Drop() => CloseWith(1006, "");

            /// <summary>The server closes the socket with this code and reason.</summary>
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

        /// <summary>
        /// haiven-api's game socket for one session, scripted: it answers auth,
        /// relays signals with the sender set by the server, and records what
        /// the controller asked for. Frames wait in a queue until settled.
        /// </summary>
        private sealed class GameServer
        {
            public const string SessionId = "s-1";

            private readonly Queue<(Player To, string Frame)> outbox = new Queue<(Player, string)>();
            private readonly List<Player> players = new List<Player>();
            private int connections;

            public GameServer() => Rest = new SessionsRest(this);

            public SessionsRest Rest { get; }

            public bool RelayOnly { get; set; }

            public DateTimeOffset? RefreshAfter { get; set; }

            public bool FailIce { get; set; }

            public bool RefuseResume { get; set; }

            public bool Duplicate { get; set; }

            public int DropSignals { get; set; }

            /// <summary>Every p2p.signal data the server received, in order.</summary>
            public List<string> SignalData { get; } = new List<string>();

            /// <summary>Every p2p.signal.ack sent: who acknowledged, the attempt and the sequence number.</summary>
            public List<string> Acks { get; } = new List<string>();

            /// <summary>Refuse the next p2p.signal copies with this code, one per copy.</summary>
            public Queue<string> RefuseSignals { get; } = new Queue<string>();

            public int IceFetches { get; private set; }

            public List<(string From, string To)> Relayed { get; } = new List<(string, string)>();

            public List<string> PeerConnected { get; } = new List<string>();

            public List<IHaivenJsonNode> Resumes { get; } = new List<IHaivenJsonNode>();

            public Player Connect(string userId)
            {
                var player = new Player(this, userId, "conn-" + userId + "-" + ++connections);
                players.Add(player);
                return player;
            }

            public Player Player(string userId) => players.Last(p => p.UserId == userId && !p.Socket.IsClosed);

            public async Task<(HaivenP2PSession Controller, Player Player)> Controller(string userId, Func<double>? clock = null)
            {
                var player = Connect(userId);
                await Settle();
                var controller = new HaivenP2PSession(new HaivenSessionsClient(Rest, 7), clock);
                controller.Attach(player.Session, player.ConnectionId);
                return (controller, player);
            }

            public int SignalsFrom(string userId) => signalsFrom.TryGetValue(userId, out var count) ? count : 0;

            private readonly Dictionary<string, int> signalsFrom = new Dictionary<string, int>();

            public void Event(string userId, string type, string payload) =>
                outbox.Enqueue((Player(userId), $"{{\"type\":\"{type}\",\"payload\":{payload}}}"));

            /// <summary>
            /// Delivers queued frames and runs the given ticks until nothing moves.
            /// A reply completes its waiter through the thread pool before the
            /// await comes back to this loop, so the last rounds wait for that hop.
            /// </summary>
            public async Task Settle(params Action[] ticks)
            {
                for (var round = 0; round < 40; round++)
                {
                    if (round >= 30) await Task.Delay(2);
                    while (outbox.Count > 0)
                    {
                        var (to, frame) = outbox.Dequeue();
                        to.Socket.Deliver(frame);
                    }
                    foreach (var tick in ticks) tick();
                    await Task.Yield();
                }
            }

            /// <summary>Runs an admission or leave to completion while the server and the controllers move.</summary>
            public async Task<T> Drive<T>(Task<T> work, params HaivenP2PSession[] controllers)
            {
                await Drive((Task)work, controllers);
                return await work;
            }

            public async Task Drive(Task work, params HaivenP2PSession[] controllers)
            {
                for (var round = 0; round < 200 && !work.IsCompleted; round++)
                {
                    await Settle(controllers.Select(c => (Action)c.Tick).ToArray());
                }
                await work;
            }

            public void Handle(Player from, string json)
            {
                var frame = HaivenJson.Parse(json);
                var type = Text(frame, "type");
                var requestId = Text(frame, "requestId");
                frame.TryGetProperty("payload", out var payload);
                switch (type)
                {
                    case "auth":
                        Reply(from, "auth.success", requestId, $"{{\"connectionId\":\"{from.ConnectionId}\",\"userId\":\"{from.UserId}\"}}");
                        break;
                    case "p2p.ice.servers":
                        IceFetches++;
                        if (FailIce)
                        {
                            Refuse(from, type, requestId, "relay_unavailable");
                            break;
                        }
                        var refresh = RefreshAfter == null ? "null" : "\"" + RefreshAfter.Value.ToString("O") + "\"";
                        Reply(from, type, requestId,
                            "{\"relayPolicy\":\"" + (RelayOnly ? "relay-only" : "auto") + "\",\"expiresAt\":null,\"refreshAfter\":" + refresh
                            + ",\"iceServers\":[{\"urls\":[\"turn:turn.example:3478\"],\"username\":\"u\",\"credential\":\"secret\"}]}");
                        break;
                    case "p2p.signal":
                        signalsFrom[from.UserId] = SignalsFrom(from.UserId) + 1;
                        SignalData.Add(Text(payload!, "data"));
                        var to = Text(payload!, "toUserId");
                        var recipient = players.LastOrDefault(p => p.UserId == to && !p.Socket.IsClosed);
                        if (recipient == null && to == "gone")
                        {
                            Refuse(from, type, requestId, "peer_not_in_session");
                            break;
                        }
                        if (RefuseSignals.Count > 0)
                        {
                            Refuse(from, type, requestId, RefuseSignals.Dequeue());
                            break;
                        }
                        Reply(from, type, requestId, "null");
                        if (DropSignals > 0)
                        {
                            DropSignals--;
                            from.Socket.Drop();
                            break;
                        }
                        if (recipient == null) break;
                        Relayed.Add((from.UserId, to));
                        var received = $"{{\"type\":\"p2p.signal.received\",\"payload\":{{\"sessionId\":\"{Text(payload!, "sessionId")}\",\"fromUserId\":\"{from.UserId}\",\"attemptId\":\"{Text(payload!, "attemptId")}\",\"seq\":{Number(payload!, "seq")},\"data\":{HaivenJson.Serialize(Text(payload!, "data"))}}}}}";
                        outbox.Enqueue((recipient, received));
                        if (Duplicate) outbox.Enqueue((recipient, received));
                        break;
                    case "p2p.signal.ack":
                        Acks.Add(from.UserId + ":" + Text(payload!, "attemptId") + ":" + Number(payload!, "seq"));
                        Reply(from, type, requestId, "null");
                        var ackTo = players.LastOrDefault(p => p.UserId == Text(payload!, "toUserId") && !p.Socket.IsClosed);
                        if (ackTo != null)
                            outbox.Enqueue((ackTo, $"{{\"type\":\"p2p.signal.ack\",\"payload\":{{\"sessionId\":\"{Text(payload!, "sessionId")}\",\"fromUserId\":\"{from.UserId}\",\"attemptId\":\"{Text(payload!, "attemptId")}\",\"seq\":{Number(payload!, "seq")}}}}}"));
                        break;
                    case "session.peer.connected":
                        PeerConnected.Add(from.UserId + ":" + Text(payload!, "userId"));
                        Reply(from, type, requestId, "null");
                        break;
                    case "session.resume":
                        Resumes.Add(payload!);
                        if (RefuseResume) Refuse(from, type, requestId, "session_not_found");
                        else Reply(from, type, requestId, $"{{\"participationId\":\"{Text(payload!, "participationId")}\",\"connectionId\":\"{from.ConnectionId}\"}}");
                        break;
                    default:
                        Refuse(from, type, requestId, "unknown_type");
                        break;
                }
            }

            private void Reply(Player to, string type, string requestId, string payload) =>
                outbox.Enqueue((to, $"{{\"type\":\"{type}\",\"requestId\":\"{requestId}\",\"success\":true,\"payload\":{payload}}}"));

            private void Refuse(Player to, string type, string requestId, string error) =>
                outbox.Enqueue((to, $"{{\"type\":\"{type}\",\"requestId\":\"{requestId}\",\"success\":false,\"error\":\"{error}\"}}"));

            private static int Number(IHaivenJsonNode node, string name) =>
                node.TryGetProperty(name, out var value) && value.Kind == HaivenJsonKind.Number ? int.Parse(value.GetNumberText()) : 0;
        }

        /// <summary>The sessions REST routes for one session, answering as the caller the test names.</summary>
        private sealed class SessionsRest : IRestApiTransport
        {
            private readonly GameServer server;
            private readonly List<string> calls = new List<string>();

            public SessionsRest(GameServer server) => this.server = server;

            public Dictionary<string, string> Bodies { get; } = new Dictionary<string, string>();

            public string[] Members { get; set; } = { "host", "guest" };

            public bool RosterGone { get; set; }

            public bool RosterUnauthorized { get; set; }

            /// <summary>The next admission reaches the server, which records it, and its reply is lost.</summary>
            public bool LoseNextReply { get; set; }

            /// <summary>The admission ids the server has taken, in order.</summary>
            public List<string> Admissions { get; } = new List<string>();

            public int Count(string call) => calls.Count(c => c == call);

            public Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
            {
                var path = request.Path.Split('?')[0];
                var call = request.Method + " " + path;
                calls.Add(call);
                if (request.Body != null) Bodies[call] = request.Body;
                if (request.Method == "POST" && request.Body != null)
                {
                    Admissions.Add(Text(HaivenJson.Parse(request.Body), "admissionId"));
                    if (LoseNextReply)
                    {
                        LoseNextReply = false;
                        throw new System.Net.Http.HttpRequestException("the connection closed before the reply");
                    }
                }
                string? body = call switch
                {
                    "GET /games/7/sessions/s-1" when RosterUnauthorized =>
                        throw new HaivenRestException(401, "{\"code\":\"unauthorized\"}"),
                    "POST /games/7/sessions" =>
                        "{\"sessionId\":\"s-1\",\"participationId\":\"p-host\",\"relayPolicy\":\"relay-only\",\"joinCode\":\"ABC123\",\"state\":\"open\"}",
                    "POST /games/7/sessions/s-1/members" =>
                        "{\"sessionId\":\"s-1\",\"participationId\":\"p-guest\",\"hostUserId\":\"host\",\"relayPolicy\":\"relay-only\",\"state\":\"open\"}",
                    "GET /games/7/sessions/s-1" when RosterGone =>
                        throw new HaivenRestException(404, "{\"code\":\"session_not_found\"}"),
                    "GET /games/7/sessions/s-1" =>
                        "{\"sessionId\":\"s-1\",\"hostUserId\":\"host\",\"members\":["
                        + string.Join(",", Members.Select(m => $"{{\"userId\":\"{m}\",\"isHost\":{(m == "host" ? "true" : "false")}}}")) + "]}",
                    "DELETE /games/7/sessions/s-1/members/me" => null,
                    _ => throw new HaivenRestException(404, "{\"code\":\"not_found\"}"),
                };
                return Task.FromResult(body);
            }

            public Uri Url(string path) => new Uri("https://api.example.test/v0" + path);
        }

        /// <summary>A single-threaded loop, as a game's main thread is: every continuation runs on it.</summary>
        internal static class Loop
        {
            public static void Run(Func<Task> body)
            {
                var previous = SynchronizationContext.Current;
                var context = new LoopContext();
                SynchronizationContext.SetSynchronizationContext(context);
                try
                {
                    var task = body();
                    context.RunUntil(task);
                    task.GetAwaiter().GetResult();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previous);
                }
            }

            private sealed class LoopContext : SynchronizationContext
            {
                private readonly BlockingCollection<(SendOrPostCallback, object?)> queue =
                    new BlockingCollection<(SendOrPostCallback, object?)>();

                public override void Post(SendOrPostCallback d, object? state) => queue.Add((d, state));

                public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

                public void RunUntil(Task task)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(30);
                    while (!task.IsCompleted)
                    {
                        if (DateTime.UtcNow > deadline) throw new TimeoutException("the test loop did not finish");
                        if (queue.TryTake(out var item, 50)) item.Item1(item.Item2);
                    }
                    while (queue.TryTake(out var item)) item.Item1(item.Item2);
                }
            }
        }
    }
}
