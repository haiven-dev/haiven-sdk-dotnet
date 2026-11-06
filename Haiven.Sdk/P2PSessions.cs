using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk
{
    /// <summary>Who may find and join a session, as haiven-api names it.</summary>
    public enum HaivenSessionVisibility
    {
        /// <summary>Listed for every player the server lets in; always relay-only.</summary>
        Public,

        /// <summary>Listed for the host's friends only.</summary>
        Friends,

        /// <summary>Never listed; joined with the six-character code.</summary>
        Private,
    }

    /// <summary>What a create asks for. Only <see cref="MaxMembers"/> has no default.</summary>
    public sealed class HaivenSessionCreateOptions
    {
        public HaivenSessionCreateOptions(int maxMembers) => MaxMembers = maxMembers;

        public int MaxMembers { get; }

        public HaivenSessionVisibility Visibility { get; set; } = HaivenSessionVisibility.Public;

        /// <summary>Allowed only for friends and private sessions; the server decides the age group itself.</summary>
        public bool AllowMixedAges { get; set; }

        /// <summary>Forces relay-only even where the server would allow direct candidates.</summary>
        public bool RequireRelay { get; set; }

        /// <summary>Handed to every guest on join, at most 512 bytes.</summary>
        public string? JoinPayload { get; set; }

        /// <summary>Shown in the session list, such as a room name.</summary>
        public Dictionary<string, string>? Metadata { get; set; }
    }

    /// <summary>The caller's place in a session, from a create or a join.</summary>
    public sealed class HaivenSessionAdmission
    {
        internal HaivenSessionAdmission(
            string sessionId, string participationId, string hostUserId, string relayPolicy,
            string? joinCode, string? joinPayload, string resumeToken, string connectionId, bool hosting)
        {
            SessionId = sessionId;
            ParticipationId = participationId;
            HostUserId = hostUserId;
            RelayPolicy = relayPolicy;
            JoinCode = joinCode;
            JoinPayload = joinPayload;
            ResumeToken = resumeToken;
            ConnectionId = connectionId;
            Hosting = hosting;
        }

        public string SessionId { get; }

        public string ParticipationId { get; }

        /// <summary>The host's user id. On the host it is read from the roster after the create, and empty if that read failed.</summary>
        public string HostUserId { get; internal set; }

        /// <summary><c>auto</c> or <c>relay-only</c>.</summary>
        public string RelayPolicy { get; }

        /// <summary>The code a private or mixed-age session is joined with; null on a guest.</summary>
        public string? JoinCode { get; }

        public string? JoinPayload { get; }

        public bool Hosting { get; }

        /// <summary>The socket the participation is bound to; it moves with every resume.</summary>
        public string ConnectionId { get; internal set; }

        /// <summary>The proof for <c>session.resume</c>, kept in memory only (SDK-P2P-2) and never logged.</summary>
        internal string ResumeToken { get; }
    }

    /// <summary>
    /// The REST half of P2P sessions (ADR 0036) for one game, over the
    /// generated <see cref="RestApi"/>. Every admission names the game socket
    /// it binds to, which must be opened with the game credential. A refusal
    /// surfaces as a <see cref="HaivenRestException"/>; branch on its <c>Code</c>.
    /// </summary>
    public sealed class HaivenSessionsClient
    {
        private readonly RestApi api;

        public HaivenSessionsClient(IRestApiTransport transport, long gameId)
            : this(new RestApi(transport ?? throw new ArgumentNullException(nameof(transport))), gameId)
        {
        }

        public HaivenSessionsClient(RestApi api, long gameId)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            GameId = gameId;
        }

        public long GameId { get; }

        /// <summary>The sessions this player may join. The server filters by age group and never lists mixed-age sessions.</summary>
        public async Task<IReadOnlyList<ListedSession>> ListAsync(int limit = 50, CancellationToken cancellationToken = default)
        {
            var list = await api.GetGamesGameIdSessionsAsync(GameId, null, limit, cancellationToken).ConfigureAwait(false);
            return (IReadOnlyList<ListedSession>?)list?.Items ?? Array.Empty<ListedSession>();
        }

        /// <summary>Creates a session with the caller as host, bound to the socket <paramref name="connectionId"/>.</summary>
        public Task<HaivenSessionAdmission> CreateAsync(
            string connectionId, HaivenSessionCreateOptions options, CancellationToken cancellationToken = default) =>
            CreateAsync(connectionId, options, NewAdmissionId(), NewResumeToken(), cancellationToken);

        /// <summary>Joins a listed session, or a private or mixed-age one with its code.</summary>
        public Task<HaivenSessionAdmission> JoinAsync(
            string connectionId, string sessionId, string? joinCode = null, CancellationToken cancellationToken = default) =>
            JoinAsync(connectionId, sessionId, joinCode, NewAdmissionId(), NewResumeToken(), cancellationToken);

        // A repeated admissionId with the same body and resumeToken returns the
        // server's first answer (SDK-P2P-3), so the controller passes them in to
        // repeat an admission whose reply was lost.
        internal async Task<HaivenSessionAdmission> CreateAsync(
            string connectionId, HaivenSessionCreateOptions options, string admissionId, string resumeToken,
            CancellationToken cancellationToken)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var created = await api.PostGamesGameIdSessionsAsync(GameId, new CreateRequest
            {
                ConnectionId = connectionId,
                AdmissionId = admissionId,
                ResumeToken = resumeToken,
                Visibility = Wire(options.Visibility),
                MaxMembers = options.MaxMembers,
                AllowMixedAges = options.AllowMixedAges && options.Visibility != HaivenSessionVisibility.Public,
                RequireRelay = options.RequireRelay ? true : (bool?)null,
                JoinPayload = options.JoinPayload,
                Metadata = options.Metadata,
            }, cancellationToken).ConfigureAwait(false) ?? throw Empty();
            return new HaivenSessionAdmission(
                created.SessionId ?? "", created.ParticipationId ?? "", "", created.RelayPolicy ?? "",
                created.JoinCode, options.JoinPayload, resumeToken, connectionId, hosting: true);
        }

        internal async Task<HaivenSessionAdmission> JoinAsync(
            string connectionId, string sessionId, string? joinCode, string admissionId, string resumeToken,
            CancellationToken cancellationToken)
        {
            var joined = await api.PostGamesGameIdSessionsSessionIdMembersAsync(GameId, sessionId, new JoinRequest
            {
                ConnectionId = connectionId,
                AdmissionId = admissionId,
                ResumeToken = resumeToken,
                JoinCode = string.IsNullOrEmpty(joinCode) ? null : joinCode,
            }, cancellationToken).ConfigureAwait(false) ?? throw Empty();
            return new HaivenSessionAdmission(
                joined.SessionId ?? sessionId, joined.ParticipationId ?? "", joined.HostUserId ?? "", joined.RelayPolicy ?? "",
                null, joined.JoinPayload, resumeToken, connectionId, hosting: false);
        }

        /// <summary>The session as a member sees it, with its members.</summary>
        public async Task<Roster> RosterAsync(string sessionId, CancellationToken cancellationToken = default) =>
            await api.GetGamesGameIdSessionsSessionIdAsync(GameId, sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw Empty();

        /// <summary>Leaves the session; the host leaving closes it for everyone.</summary>
        public Task LeaveAsync(string sessionId, CancellationToken cancellationToken = default) =>
            api.DeleteGamesGameIdSessionsSessionIdMembersMeAsync(GameId, sessionId, cancellationToken);

        internal static string Wire(HaivenSessionVisibility visibility) => visibility switch
        {
            HaivenSessionVisibility.Public => "public",
            HaivenSessionVisibility.Friends => "friends",
            _ => "private",
        };

        internal static string NewAdmissionId() => Guid.NewGuid().ToString("D");

        // ADR 0036 point 4: 32 bytes from the platform CSPRNG, unpadded base64url.
        internal static string NewResumeToken()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static InvalidOperationException Empty() => new InvalidOperationException("haiven: the session response is empty");
    }

    /// <summary>
    /// The peer connections a game's networking layer holds, as the controller
    /// needs to see them. The game's transport adapter implements it.
    /// </summary>
    public interface IHaivenPeerRoster
    {
        /// <summary>The user ids of the peers connected now.</summary>
        IEnumerable<string> ConnectedUsers();

        /// <summary>Closes the connection to one peer. False when there was none.</summary>
        bool DropUser(string userId);
    }

    /// <summary>What the controller is doing.</summary>
    public enum HaivenP2PState
    {
        /// <summary>Not in a session.</summary>
        Idle,

        /// <summary>A create or join is running.</summary>
        Admitting,

        InSession,

        /// <summary>The game socket dropped; the participation resumes on the next one within 30 seconds.</summary>
        Resuming,
    }

    /// <summary>Why the controller left a session.</summary>
    public enum HaivenP2PEndReason
    {
        /// <summary>The game called <see cref="HaivenP2PSession.LeaveAsync"/>.</summary>
        Left,

        /// <summary><c>session.closed</c>, or a roster read found the session gone.</summary>
        Closed,

        /// <summary><c>session.member.left</c> named this participation.</summary>
        Removed,

        /// <summary>The server refused <c>session.resume</c>.</summary>
        ResumeRefused,

        /// <summary>No socket came back within the resume window.</summary>
        ResumeWindowElapsed,

        /// <summary>
        /// The credential no longer authenticates: the game socket closed with
        /// <c>1008 credential_revoked</c> or <c>1008 unauthorized</c>, or a REST call
        /// answered <c>401</c> (SDK-P2P-6). The game signs the player in again.
        /// </summary>
        SignedOut,

        /// <summary>
        /// The server refused the game socket with another <c>1008</c>, such as
        /// <c>parental_consent_denied</c>. A new socket with the same credential is refused too.
        /// </summary>
        SocketRefused,
    }

    /// <summary>
    /// The P2P controller of the haiven-sdk specification: one participation
    /// in one session, its signaling, its ICE servers, its resume and its
    /// roster, over the game socket and the sessions REST routes.
    ///
    /// It holds no engine or networking-stack type. The game drives it from
    /// one thread: it calls <see cref="Tick"/> every frame, gives it each new
    /// game socket with <see cref="Attach"/>, hands <see cref="Signaling"/> and
    /// <see cref="IceOptions"/> to its transport adapter, reports the open data
    /// channel with <see cref="ReportPeerConnected"/>, and sets
    /// <see cref="Peers"/> so the host can drop peers the server removed.
    /// Every event is raised on that thread.
    /// </summary>
    public sealed class HaivenP2PSession : IDisposable
    {
        /// <summary>How long the server holds a dropped participation (ADR 0036 point 7).</summary>
        public const double ResumeWindowSeconds = 30;

        /// <summary>How often the host reads the roster (ADR 0036 point 16).</summary>
        public const double RosterIntervalSeconds = 30;

        /// <summary>How long to wait before fetching ICE servers again after a failed fetch (SDK-P2P-14).</summary>
        public const double IceRetrySeconds = 30;

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        private readonly HaivenSessionsClient sessions;
        private readonly Func<double> clock;
        private HaivenSessionChannel? channel;
        private HaivenSessionAdmission? room;
        private double? lostAt;
        private double nextRoster;
        private double? refreshIceAt;
        private bool resumeInFlight;
        private bool rosterRunning;
        private bool iceRunning;
        private bool disposed;
        private Admission? unanswered;

        /// <param name="sessions">The sessions REST client for this game.</param>
        /// <param name="clock">Seconds from any fixed point; a monotonic clock when null.</param>
        public HaivenP2PSession(HaivenSessionsClient sessions, Func<double>? clock = null)
        {
            this.sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            this.clock = clock ?? HaivenSessionSignaling.MonotonicSeconds();
        }

        public HaivenP2PState State { get; private set; } = HaivenP2PState.Idle;

        /// <summary>The session joined or hosted; null when idle.</summary>
        public HaivenSessionAdmission? Room => room;

        /// <summary>The current game socket channel; null before the first <see cref="Attach"/>.</summary>
        public HaivenSessionChannel? Channel => channel;

        /// <summary>The signaling for the transport adapter while in a session.</summary>
        public HaivenSessionSignaling? Signaling { get; private set; }

        /// <summary>ICE servers and policy for new peer connections while in a session.</summary>
        public PeerConnectionOptions? IceOptions { get; private set; }

        /// <summary>The game's peer connections; the host drops through it.</summary>
        public IHaivenPeerRoster? Peers { get; set; }

        /// <summary>New ICE servers for the next peer connection; open connections keep theirs (SDK-P2P-12).</summary>
        public event Action<PeerConnectionOptions>? IceOptionsChanged;

        /// <summary>A member joined: their user id.</summary>
        public event Action<string>? MemberJoined;

        /// <summary>
        /// A member is gone: their user id and the server's reason, or
        /// <c>not_on_roster</c> when the host's roster read found them missing.
        /// On the host their connection is already dropped through <see cref="Peers"/>.
        /// </summary>
        public event Action<string, string>? MemberLeft;

        /// <summary>The game socket dropped while in a session; the controller resumes on the next one.</summary>
        public event Action? ConnectionLost;

        /// <summary>The participation moved to the new socket.</summary>
        public event Action? Resumed;

        /// <summary>The session ended for this player: why, and the server's code or reason when it gave one.</summary>
        public event Action<HaivenP2PEndReason, string?>? Ended;

        /// <summary>A background request failed and will be tried again: what failed.</summary>
        public event Action<string, Exception>? Diagnostic;

        /// <summary>
        /// Uses a game socket, the first one or the one that replaced a dropped
        /// socket. In a session, the participation resumes on it once it has
        /// authenticated. Pass <paramref name="connectionId"/> when the socket
        /// authenticated before this call.
        /// </summary>
        public void Attach(HaivenSessionCore session, string? connectionId = null)
        {
            if (session is null) throw new ArgumentNullException(nameof(session));
            ThrowIfDisposed();
            var previous = channel;
            channel = new HaivenSessionChannel(session, connectionId);
            channel.Event += OnEvent;
            channel.Lost += OnLost;
            if (previous != null)
            {
                previous.Event -= OnEvent;
                previous.Lost -= OnLost;
                previous.Dispose();
                if (room != null) Lose(previous.CloseKind, previous.CloseReason);
            }
        }

        /// <summary>
        /// Creates a session and hosts it. When the reply is lost, calling it
        /// again with the same options on the same socket repeats the same
        /// admission, and the server answers as it did the first time (SDK-P2P-3).
        /// </summary>
        public async Task<HaivenSessionAdmission> CreateAsync(HaivenSessionCreateOptions options, CancellationToken cancellationToken = default)
        {
            if (options is null) throw new ArgumentNullException(nameof(options));
            var connectionId = BeginAdmission();
            try
            {
                var attempt = Repeat("create", connectionId, CreateKey(options));
                var admission = await Admit(attempt, () =>
                    sessions.CreateAsync(connectionId, options, attempt.AdmissionId, attempt.ResumeToken, cancellationToken));
                try
                {
                    var roster = await sessions.RosterAsync(admission.SessionId, cancellationToken);
                    admission.HostUserId = roster.HostUserId ?? "";
                }
                catch (Exception error) when (!(error is OperationCanceledException))
                {
                    Diagnostic?.Invoke("roster", error);
                }
                await EnterAsync(admission);
                return admission;
            }
            catch
            {
                if (State == HaivenP2PState.Admitting) State = HaivenP2PState.Idle;
                throw;
            }
        }

        /// <summary>
        /// Joins a listed session, or a private or mixed-age one with its code.
        /// When the reply is lost, calling it again with the same arguments on
        /// the same socket repeats the same admission (SDK-P2P-3).
        /// </summary>
        public async Task<HaivenSessionAdmission> JoinAsync(string sessionId, string? joinCode = null, CancellationToken cancellationToken = default)
        {
            var connectionId = BeginAdmission();
            try
            {
                var attempt = Repeat("join", connectionId, sessionId + "\n" + joinCode);
                var admission = await Admit(attempt, () =>
                    sessions.JoinAsync(connectionId, sessionId, joinCode, attempt.AdmissionId, attempt.ResumeToken, cancellationToken));
                await EnterAsync(admission);
                return admission;
            }
            catch
            {
                if (State == HaivenP2PState.Admitting) State = HaivenP2PState.Idle;
                throw;
            }
        }

        /// <summary>Leaves the session. The host leaving closes it for everyone.</summary>
        public async Task LeaveAsync()
        {
            var left = room;
            if (left == null) return;
            End(HaivenP2PEndReason.Left, null);
            try
            {
                await sessions.LeaveAsync(left.SessionId);
            }
            catch (HaivenRestException error) when (error.Code == "session_not_found")
            {
                // Already over on the server: the host closed it, or it timed out.
            }
        }

        /// <summary>
        /// Host side: the data channels to <paramref name="userId"/> are open.
        /// A guest the host never reports loses its place after 60 seconds (ADR 0036 point 15).
        /// </summary>
        public void ReportPeerConnected(string userId)
        {
            if (room == null || !room.Hosting || channel == null) return;
            channel.Send(WsTypes.SessionPeerConnected, new Contracts.SessionPeerConnectedV0
            {
                SessionId = room.SessionId,
                UserId = userId,
            });
        }

        /// <summary>Runs queued socket events, the resume, the ICE refresh and the host's roster check. Call it every frame.</summary>
        public void Tick()
        {
            if (disposed) return;
            channel?.Pump();
            if (room == null) return;
            var now = clock();
            if (State == HaivenP2PState.Resuming)
            {
                KeepResuming(now);
                return;
            }
            // While admitting, the room is known before its ICE servers are,
            // and the roster schedule starts only once the session is entered.
            if (State != HaivenP2PState.InSession) return;
            if (refreshIceAt != null && now >= refreshIceAt) _ = RefreshIceAsync();
            if (room.Hosting && now >= nextRoster) _ = CheckRosterAsync();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Signaling?.Stop();
            if (channel != null)
            {
                channel.Event -= OnEvent;
                channel.Lost -= OnLost;
                channel.Dispose();
            }
        }

        /// <summary>Connected peers the roster no longer lists.</summary>
        public static IReadOnlyList<string> NotOnRoster(IEnumerable<string> connected, IEnumerable<string> roster)
        {
            var members = new HashSet<string>(roster);
            return connected.Where(user => !members.Contains(user)).ToList();
        }

        /// <summary>Reads a <c>p2p.ice.servers</c> reply into connection settings and when to fetch them again.</summary>
        public static PeerConnectionOptions ReadIceServers(Contracts.P2pIceServersReplyV0 reply, out DateTimeOffset? refreshAt)
        {
            if (reply is null) throw new ArgumentNullException(nameof(reply));
            var servers = new List<PeerIceServer>();
            foreach (var server in reply.IceServers ?? Array.Empty<Contracts.P2pIceServersReplyV0IceServersItem>())
            {
                if (server?.Urls == null || server.Urls.Length == 0) continue;
                servers.Add(new PeerIceServer(server.Urls, server.Username, server.Credential));
            }
            // The generated reader keeps refreshAfter as the JSON node it read: a string or null.
            var text = reply.RefreshAfter switch
            {
                string value => value,
                Json.IHaivenJsonNode { Kind: Json.HaivenJsonKind.String } node => node.GetString(),
                _ => null,
            };
            refreshAt = text != null
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                    ? at
                    : (DateTimeOffset?)null;
            var policy = reply.RelayPolicy == "relay-only" ? PeerIcePolicy.Relay : PeerIcePolicy.All;
            return new PeerConnectionOptions(servers, policy);
        }

        private string BeginAdmission()
        {
            ThrowIfDisposed();
            if (State != HaivenP2PState.Idle) throw new InvalidOperationException("haiven: already in a session");
            var connectionId = channel?.IsOpen == true ? channel.ConnectionId : null;
            // SDK-P2P-1: admission binds the participation to this socket.
            if (connectionId == null) throw new InvalidOperationException("haiven: the game socket has not authenticated");
            State = HaivenP2PState.Admitting;
            return connectionId;
        }

        // The admission the server may have taken without its reply arriving.
        // Only the same call on the same socket repeats it: the server answers
        // a different body under the same admissionId with 409.
        private Admission Repeat(string kind, string connectionId, string arguments)
        {
            var key = kind + "\n" + connectionId + "\n" + arguments;
            return unanswered != null && unanswered.Key == key
                ? unanswered
                : new Admission(key, HaivenSessionsClient.NewAdmissionId(), HaivenSessionsClient.NewResumeToken());
        }

        private async Task<HaivenSessionAdmission> Admit(Admission attempt, Func<Task<HaivenSessionAdmission>> send)
        {
            try
            {
                var admission = await send();
                unanswered = null;
                return admission;
            }
            catch (HaivenRestException)
            {
                // The server answered, so a new call is a new admission.
                unanswered = null;
                throw;
            }
            catch
            {
                // No answer: the server may have admitted us.
                unanswered = attempt;
                throw;
            }
        }

        private static string CreateKey(HaivenSessionCreateOptions options)
        {
            var metadata = options.Metadata == null
                ? ""
                : string.Join("\n", options.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            return string.Join("\n", HaivenSessionsClient.Wire(options.Visibility), options.MaxMembers, options.AllowMixedAges,
                options.RequireRelay, options.JoinPayload, metadata);
        }

        private sealed class Admission
        {
            public Admission(string key, string admissionId, string resumeToken)
            {
                Key = key;
                AdmissionId = admissionId;
                ResumeToken = resumeToken;
            }

            public string Key { get; }

            public string AdmissionId { get; }

            public string ResumeToken { get; }
        }

        private async Task EnterAsync(HaivenSessionAdmission admission)
        {
            room = admission;
            try
            {
                IceOptions = await FetchIceAsync(admission);
            }
            catch
            {
                room = null;
                try
                {
                    await sessions.LeaveAsync(admission.SessionId);
                }
                catch (Exception error)
                {
                    Diagnostic?.Invoke("leave", error);
                }
                throw;
            }
            Signaling = new HaivenSessionSignaling(channel!, admission.SessionId,
                admission.Hosting ? admission.HostUserId : "", admission.Hosting ? null : admission.HostUserId, clock,
                admission.ParticipationId);
            Signaling.Start(admission.Hosting);
            nextRoster = clock() + RosterIntervalSeconds;
            State = HaivenP2PState.InSession;
        }

        // haiven-api issues the servers for the caller's participation on this
        // socket. A credential is checked only when an allocation is made, so a
        // fresh one matters for the next guest, not for connections already open.
        private async Task<PeerConnectionOptions> FetchIceAsync(HaivenSessionAdmission admission)
        {
            var reply = await channel!.RequestAsync(WsTypes.P2pIceServers, null, RequestTimeout);
            if (reply.Success != true) throw new InvalidOperationException(reply.Error ?? "p2p.ice.servers failed");
            var decoded = WsApi.DecodeP2pIceServersReply(reply)
                ?? throw new InvalidOperationException("haiven: p2p.ice.servers returned no servers");
            var options = ReadIceServers(decoded, out var refreshAt);
            refreshIceAt = refreshAt == null
                ? (double?)null
                : clock() + Math.Max(0, (refreshAt.Value - DateTimeOffset.UtcNow).TotalSeconds);
            return options;
        }

        private async Task RefreshIceAsync()
        {
            if (iceRunning || room == null) return;
            iceRunning = true;
            refreshIceAt = null;
            var current = room;
            try
            {
                var options = await FetchIceAsync(current);
                if (room != current) return;
                IceOptions = options;
                IceOptionsChanged?.Invoke(options);
            }
            catch (Exception error)
            {
                Diagnostic?.Invoke("ice", error);
                if (room == current) refreshIceAt = clock() + IceRetrySeconds;
            }
            finally
            {
                iceRunning = false;
            }
        }

        private void OnLost() => Lose(channel?.CloseKind, channel?.CloseReason);

        // A plain drop, 1013 auth_unavailable included, keeps the credential, so
        // the participation resumes. After any 1008 a new socket with this
        // credential is refused too, so there is nothing to resume on (SDK-P2P-6).
        private void Lose(HaivenSessionCloseKind? kind, string? reason)
        {
            if (room == null) return;
            if (kind != null && !HaivenSessionClose.MayReconnect(kind.Value))
            {
                End(HaivenSessionClose.RequiresSignIn(kind.Value) ? HaivenP2PEndReason.SignedOut : HaivenP2PEndReason.SocketRefused, reason);
                return;
            }
            if (State == HaivenP2PState.Resuming) return;
            // The peer connections keep running meanwhile (SDK-P2P-5).
            State = HaivenP2PState.Resuming;
            lostAt = clock();
            ConnectionLost?.Invoke();
        }

        private void KeepResuming(double now)
        {
            if (resumeInFlight || room == null) return;
            if (lostAt != null && now - lostAt > ResumeWindowSeconds)
            {
                End(HaivenP2PEndReason.ResumeWindowElapsed, null);
                return;
            }
            if (channel != null && channel.IsOpen && channel.ConnectionId != room.ConnectionId) _ = ResumeAsync();
        }

        private async Task ResumeAsync()
        {
            resumeInFlight = true;
            var resumed = channel!;
            var current = room!;
            try
            {
                var reply = await resumed.RequestAsync(WsTypes.SessionResume, new Contracts.SessionResumeRequestV0
                {
                    ParticipationId = current.ParticipationId,
                    PreviousConnectionId = current.ConnectionId,
                    ResumeToken = current.ResumeToken,
                }, RequestTimeout);
                if (room != current) return;
                if (reply.Success != true)
                {
                    // session_not_found covers every refusal: the window ended,
                    // the session closed, or another socket resumed first.
                    End(HaivenP2PEndReason.ResumeRefused, reply.Error);
                    return;
                }
                var moved = WsApi.DecodeSessionResumeReply(reply)?.ConnectionId;
                current.ConnectionId = !string.IsNullOrEmpty(moved) ? moved! : resumed.ConnectionId ?? "";
                Signaling?.Rebind(resumed);
                State = HaivenP2PState.InSession;
                lostAt = null;
                // After a resume the host reads the roster at once (SDK-P2P-10):
                // an event sent during the gap may be lost.
                nextRoster = 0;
                Resumed?.Invoke();
            }
            catch (Exception error) when (error is TimeoutException || error is InvalidOperationException || error is TaskCanceledException)
            {
                // The new socket closed or did not answer; the next one tries again while the window lasts.
                Diagnostic?.Invoke("resume", error);
            }
            finally
            {
                resumeInFlight = false;
            }
        }

        // The host closes the connection to anyone the server no longer lists,
        // in case a session.member.left was lost (ADR 0036 point 16).
        private async Task CheckRosterAsync()
        {
            if (rosterRunning || room == null) return;
            rosterRunning = true;
            nextRoster = clock() + RosterIntervalSeconds;
            var current = room;
            try
            {
                var roster = await sessions.RosterAsync(current.SessionId);
                if (room != current || Peers == null) return;
                var members = (roster.Members ?? new List<RosterMember>())
                    .Select(member => member.UserId)
                    .Where(userId => !string.IsNullOrEmpty(userId))
                    .Select(userId => userId!);
                foreach (var userId in NotOnRoster(Peers.ConnectedUsers(), members))
                {
                    Peers.DropUser(userId);
                    MemberLeft?.Invoke(userId, "not_on_roster");
                }
            }
            catch (HaivenRestException error) when (error.Code == "session_not_found")
            {
                if (room == current) End(HaivenP2PEndReason.Closed, error.Code);
            }
            catch (HaivenRestException error) when (error.StatusCode == 401)
            {
                if (room == current) End(HaivenP2PEndReason.SignedOut, error.Code ?? "unauthorized");
            }
            catch (Exception error)
            {
                Diagnostic?.Invoke("roster", error);
            }
            finally
            {
                rosterRunning = false;
            }
        }

        private void OnEvent(ServerFrame frame)
        {
            if (room == null || frame.Payload == null) return;
            try
            {
                switch (frame.Type)
                {
                    case WsTypes.SessionMemberJoined:
                        var joined = Json.ContractsJson.ReadSessionMemberJoinedV0(frame.Payload, "payload");
                        if (joined.SessionId == room.SessionId) MemberJoined?.Invoke(joined.UserId);
                        break;
                    case WsTypes.SessionMemberLeft:
                        OnMemberLeft(Json.ContractsJson.ReadSessionMemberLeftV0(frame.Payload, "payload"));
                        break;
                    case WsTypes.SessionClosed:
                        var closed = Json.ContractsJson.ReadSessionClosedV0(frame.Payload, "payload");
                        if (closed.SessionId == room.SessionId) End(HaivenP2PEndReason.Closed, closed.Reason);
                        break;
                }
            }
            catch (Json.HaivenJsonException error)
            {
                Diagnostic?.Invoke(frame.Type, error);
            }
        }

        // The server ended this participation; close the peer at once rather
        // than wait for the connection to fail (SDK-P2P-11).
        private void OnMemberLeft(Contracts.SessionMemberLeftV0 left)
        {
            if (room == null || left.SessionId != room.SessionId) return;
            if (left.ParticipationId == room.ParticipationId)
            {
                End(HaivenP2PEndReason.Removed, left.Reason);
                return;
            }
            if (!room.Hosting && left.UserId == room.HostUserId)
            {
                End(HaivenP2PEndReason.Closed, left.Reason);
                return;
            }
            if (room.Hosting) Peers?.DropUser(left.UserId);
            MemberLeft?.Invoke(left.UserId, left.Reason);
        }

        private void End(HaivenP2PEndReason reason, string? code)
        {
            if (room == null) return;
            room = null;
            Signaling?.Stop();
            Signaling = null;
            IceOptions = null;
            refreshIceAt = null;
            lostAt = null;
            State = HaivenP2PState.Idle;
            Ended?.Invoke(reason, code);
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(HaivenP2PSession));
        }
    }
}
