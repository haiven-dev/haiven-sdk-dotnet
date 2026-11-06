#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk
{
    /// <summary>
    /// A non-2xx REST response, as an <see cref="IRestApiTransport"/> should
    /// report one: the status and the error body's finite <c>code</c>
    /// (<c>ErrorResponse.code</c>). <see cref="RewardClaims"/> classifies a
    /// refusal only from this; any other exception reads as a transport failure.
    /// </summary>
    public sealed class HaivenRestException : Exception
    {
        public HaivenRestException(int statusCode, string? body, string? message = null)
            : base(message ?? "haiven: the request failed with HTTP " + statusCode + ".")
        {
            StatusCode = statusCode;
            Body = body;
            Code = CodeOf(body);
        }

        /// <summary>The HTTP status.</summary>
        public int StatusCode { get; }

        /// <summary>The response body text, or null when there was none.</summary>
        public string? Body { get; }

        /// <summary>The error body's code, verbatim; null when it has none. Branch on this, never on the message.</summary>
        public string? Code { get; }

        private static string? CodeOf(string? body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                var root = HaivenJson.Parse(body!);
                return root.Kind == HaivenJsonKind.Object
                    && root.TryGetProperty("code", out var code)
                    && code.Kind == HaivenJsonKind.String
                        ? code.GetString()
                        : null;
            }
            catch (HaivenJsonException)
            {
                return null;
            }
        }
    }

    /// <summary>How one reward claim, grant, or confirmation ended.</summary>
    public enum RewardClaimResult
    {
        /// <summary>
        /// Recorded: a new claim, or the claim already recorded under the same
        /// claim id (the service answers a repeat with the recorded claim).
        /// </summary>
        Claimed,

        /// <summary>The account holds no active Haiven subscription (<c>reward_not_eligible</c>).</summary>
        NotEligible,

        /// <summary>This player already claimed this reward in this period (<c>reward_already_claimed</c>).</summary>
        AlreadyClaimed,

        /// <summary>The claim id was used for a different claim (<c>reward_claim_conflict</c>).</summary>
        ClaimConflict,

        /// <summary>The period is being refunded, so it takes no new claims (<c>refund_in_progress</c>).</summary>
        RefundInProgress,

        /// <summary>The claim to confirm does not exist, or belongs to another integration.</summary>
        NotFound,

        /// <summary>Anything else: transport failure, server error, bad request, unreadable reply.</summary>
        Failed,
    }

    /// <summary>Who vouched for a claim, from <c>RewardClaimResponse.confirmation</c>.</summary>
    public enum RewardConfirmation
    {
        /// <summary>Only the player's client vouched for it.</summary>
        ClientReported,

        /// <summary>The game's server confirmed it, or granted it.</summary>
        ServerConfirmed,

        /// <summary>Absent, or a value minted after this build: never read as confirmed.</summary>
        Unrecognized,
    }

    /// <summary>One finished reward request: its result and, on success, the recorded claim.</summary>
    public sealed class RewardClaimOutcome
    {
        internal RewardClaimOutcome(
            RewardClaimResult result,
            RewardClaimResponse? claim = null,
            int? statusCode = null,
            string? errorCode = null,
            bool isRetryable = false,
            Exception? exception = null)
        {
            Result = result;
            Claim = claim;
            Confirmation = RewardClaims.ConfirmationOf(claim?.Confirmation);
            StatusCode = statusCode;
            ErrorCode = errorCode;
            IsRetryable = isRetryable;
            Exception = exception;
        }

        public RewardClaimResult Result { get; }

        /// <summary>The recorded claim, when <see cref="Result"/> is <see cref="RewardClaimResult.Claimed"/>.</summary>
        public RewardClaimResponse? Claim { get; }

        public RewardConfirmation Confirmation { get; }

        /// <summary>The HTTP status of a refusal; null on success or a transport failure.</summary>
        public int? StatusCode { get; }

        /// <summary>The refusal's finite error code, verbatim; null when none.</summary>
        public string? ErrorCode { get; }

        /// <summary>
        /// Repeating the same request may succeed. A retry through
        /// <see cref="RewardClaims.ClaimAsync"/> reuses the claim id, so it cannot claim twice.
        /// </summary>
        public bool IsRetryable { get; }

        /// <summary>What failed, for a log; never branch on its message.</summary>
        public Exception? Exception { get; }

        public bool Succeeded => Result == RewardClaimResult.Claimed;
    }

    /// <summary>
    /// A reward the player asked for before linking a Haiven account. Local
    /// state only: it is not a recorded claim, and it is not loot.
    /// </summary>
    public sealed class SavedRewardClaim
    {
        internal SavedRewardClaim(string claimId, string integrationId, string rewardRef)
        {
            ClaimId = claimId;
            IntegrationId = integrationId;
            RewardRef = rewardRef;
        }

        public string ClaimId { get; }
        public string IntegrationId { get; }
        public string RewardRef { get; }
    }

    /// <summary>The JSON document of saved claim intents. The SDK does not choose where it lives.</summary>
    public interface IRewardClaimIntentStore
    {
        /// <summary>The last saved document, or empty when nothing has been saved.</summary>
        string Load();

        /// <summary>Replaces the document. The text is the JSON array from <see cref="RewardClaims"/>.</summary>
        void Save(string document);
    }

    /// <summary>An in-memory intent document, for tests and for a process that does not outlive the intent.</summary>
    public sealed class MemoryRewardClaimIntentStore : IRewardClaimIntentStore
    {
        public string Document { get; private set; } = "";

        public string Load() => Document;

        public void Save(string document) => Document = document ?? "";
    }

    /// <summary>
    /// A file of saved claim intents. Claim ids are not credentials. A missing
    /// or unreadable file loads as no intents; the next save replaces it.
    /// </summary>
    public sealed class FileRewardClaimIntentStore : IRewardClaimIntentStore
    {
        private readonly string path;

        public FileRewardClaimIntentStore(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("A path is required.", nameof(path));
            this.path = path;
        }

        public string Load()
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : "";
            }
            catch (IOException)
            {
                return "";
            }
            catch (UnauthorizedAccessException)
            {
                return "";
            }
        }

        public void Save(string document)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, document ?? "");
        }
    }

    /// <summary>
    /// Subscriber-reward claims over the generated <see cref="RestApi"/>.
    ///
    /// A player claims a reward with <c>POST /users/me/rewards/claims</c> under a
    /// claim id the client mints (a UUIDv7). The service records one claim per
    /// player, game and period and answers a repeat of the same claim id with the
    /// recorded claim, so a retry is safe only when it reuses the id. This keeps
    /// the id of every claim that has not had a final answer, keyed by
    /// integration and reward, and hands it to the next attempt; a final answer
    /// (recorded, or refused) releases it.
    ///
    /// <see cref="Save"/> records that id before the player links and does not
    /// post it. An <see cref="IRewardClaimIntentStore"/> keeps it across launches.
    /// After the login handoff, <see cref="SubmitSavedAsync"/> posts each saved
    /// id on the same user claim route. Loot is granted only when the result is
    /// <see cref="RewardClaimResult.Claimed"/>. <see cref="Clear"/> drops in-flight
    /// ids and leaves saved intents, so the next link submits the same claim id.
    ///
    /// Refusals are classified from <see cref="HaivenRestException"/>, which the
    /// transport should throw for a non-2xx response; any other exception is a
    /// retryable failure. Nothing here throws for a network or service failure:
    /// the outcome says what happened. Cancelling throws
    /// <see cref="OperationCanceledException"/> and keeps the claim id pending,
    /// because the claim may still have been recorded. Safe to use from any thread.
    /// </summary>
    public sealed class RewardClaims
    {
        private readonly RestApi api;
        private readonly Func<string> newClaimId;
        private readonly IRewardClaimIntentStore? savedStore;
        private readonly Dictionary<(string IntegrationId, string RewardRef), string> pending =
            new Dictionary<(string, string), string>();
        private readonly Dictionary<(string IntegrationId, string RewardRef), string> saved =
            new Dictionary<(string, string), string>();
        private readonly List<(string IntegrationId, string RewardRef)> savedOrder =
            new List<(string, string)>();
        private readonly object gate = new object();
        private long sessionGeneration;

        /// <param name="api">A REST API whose transport sends the player's bearer token.</param>
        /// <param name="newClaimId">Mints claim ids; defaults to <see cref="NewUuidV7"/>.</param>
        /// <param name="savedClaims">Where pre-link intents survive a restart. Omitted, they last for this process.</param>
        public RewardClaims(RestApi api, Func<string>? newClaimId = null, IRewardClaimIntentStore? savedClaims = null)
        {
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            this.newClaimId = newClaimId ?? NewUuidV7;
            savedStore = savedClaims;
            LoadSaved();
        }

        /// <summary>Raised, on the completing thread, for every recorded claim. This is the loot signal.</summary>
        public event Action<RewardClaimOutcome>? Claimed;

        /// <summary>
        /// Raised for every submitted claim that still belongs to this player, including a refusal.
        /// A refusal is not loot.
        /// </summary>
        public event Action<RewardClaimOutcome>? ClaimSettled;

        /// <summary>Whether the signed-in account may receive subscriber rewards.</summary>
        public Task<RewardEligibilityResponse?> GetEligibilityAsync(CancellationToken cancellationToken = default) =>
            api.GetUsersMeRewardsEligibilityAsync(cancellationToken);

        /// <summary>
        /// Claims one reward for the signed-in player, as <c>client_reported</c>,
        /// reusing the claim id of an earlier attempt that had no final answer.
        /// </summary>
        public async Task<RewardClaimOutcome> ClaimAsync(
            string integrationId,
            string rewardRef,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(integrationId)) throw new ArgumentException("An integration id is required.", nameof(integrationId));
            if (string.IsNullOrEmpty(rewardRef) || UnicodeCodePointCount(rewardRef) > 128)
                throw new ArgumentException("A reward ref must be 1 to 128 characters.", nameof(rewardRef));

            var key = (integrationId, rewardRef);
            string claimId;
            long requestSessionGeneration;
            lock (gate)
            {
                requestSessionGeneration = sessionGeneration;
                if (!pending.TryGetValue(key, out claimId!))
                {
                    if (!saved.TryGetValue(key, out claimId!)) claimId = MintClaimId();
                    pending[key] = claimId;
                }
            }

            var request = new ClaimRewardRequest { ClaimId = claimId, IntegrationId = integrationId, RewardRef = rewardRef };
            var outcome = await SendAsync(() => api.PostUsersMeRewardsClaimsAsync(request, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            lock (gate)
            {
                if (requestSessionGeneration != sessionGeneration)
                    throw new OperationCanceledException("The reward claim was invalidated because the player signed out.");
                // Only the attempt that owns the id releases it.
                if (!outcome.IsRetryable && pending.TryGetValue(key, out var current) && current == claimId) pending.Remove(key);
                if (!outcome.IsRetryable) ReleaseSaved(key, claimId);
                if (ClaimSettled is not null)
                {
                    foreach (Action<RewardClaimOutcome> handler in ClaimSettled.GetInvocationList())
                    {
                        handler(outcome);
                        if (requestSessionGeneration != sessionGeneration)
                            throw new OperationCanceledException("The reward claim was invalidated because the player signed out.");
                    }
                }
                if (outcome.Succeeded && Claimed is not null)
                {
                    // Keep Clear mutually exclusive with delivery, but check after
                    // every re-entrant handler: one handler may sign out player A
                    // and start player B before the next handler runs.
                    foreach (Action<RewardClaimOutcome> handler in Claimed.GetInvocationList())
                    {
                        handler(outcome);
                        if (requestSessionGeneration != sessionGeneration)
                            throw new OperationCanceledException("The reward claim was invalidated because the player signed out.");
                    }
                }
            }
            return outcome;
        }

        /// <summary>The claim id the next claim of this reward will reuse, or null when it mints a new one.</summary>
        public string? PendingClaimId(string integrationId, string rewardRef)
        {
            lock (gate) return pending.TryGetValue((integrationId, rewardRef), out var id) ? id : null;
        }

        /// <summary>
        /// Records a claim intent before the player links. Reuses the claim id
        /// already saved or pending for this reward. Does not post, and does not
        /// raise <see cref="Claimed"/>.
        /// </summary>
        public SavedRewardClaim Save(string integrationId, string rewardRef)
        {
            if (string.IsNullOrEmpty(integrationId)) throw new ArgumentException("An integration id is required.", nameof(integrationId));
            if (string.IsNullOrEmpty(rewardRef) || UnicodeCodePointCount(rewardRef) > 128)
                throw new ArgumentException("A reward ref must be 1 to 128 characters.", nameof(rewardRef));
            var key = (integrationId, rewardRef);
            lock (gate)
            {
                if (!saved.TryGetValue(key, out var claimId))
                {
                    if (!pending.TryGetValue(key, out claimId!)) claimId = MintClaimId();
                    saved[key] = claimId;
                    savedOrder.Add(key);
                    PersistSaved();
                }
                return new SavedRewardClaim(claimId, integrationId, rewardRef);
            }
        }

        /// <summary>Saved intents in the order they were first recorded.</summary>
        public IReadOnlyList<SavedRewardClaim> SavedClaims()
        {
            lock (gate)
            {
                var copy = new List<SavedRewardClaim>(savedOrder.Count);
                foreach (var key in savedOrder)
                    copy.Add(new SavedRewardClaim(saved[key], key.IntegrationId, key.RewardRef));
                return copy;
            }
        }

        /// <summary>
        /// Posts every saved intent on <c>POST /users/me/rewards/claims</c> under
        /// the id <see cref="Save"/> kept. Call this after the login handoff.
        /// Each outcome is the server's answer; only <see cref="RewardClaimResult.Claimed"/> is loot.
        /// </summary>
        public async Task<IReadOnlyList<RewardClaimOutcome>> SubmitSavedAsync(CancellationToken cancellationToken = default)
        {
            (string IntegrationId, string RewardRef)[] batch;
            lock (gate) batch = savedOrder.ToArray();
            var outcomes = new List<RewardClaimOutcome>(batch.Length);
            foreach (var item in batch)
            {
                lock (gate)
                {
                    if (!saved.ContainsKey(item)) continue;
                }
                outcomes.Add(await ClaimAsync(item.IntegrationId, item.RewardRef, cancellationToken).ConfigureAwait(false));
            }
            return outcomes;
        }

        /// <summary>
        /// Submits saved intents when the handoff stores a credential. Refusals
        /// are raised on <see cref="ClaimSettled"/>; loot stays on <see cref="Claimed"/>.
        /// </summary>
        public void Follow(HandoffCoordinator handoff)
        {
            if (handoff is null) throw new ArgumentNullException(nameof(handoff));
            handoff.Completed += _ => Observe(SubmitSavedAsync());
        }

        /// <summary>
        /// Forgets every pending claim id because the player signed out. An outstanding
        /// <see cref="ClaimAsync"/> then throws <see cref="OperationCanceledException"/> instead
        /// of reporting its result to the next signed-in player's session. Saved
        /// intents stay, so the next link submits the same claim id.
        /// </summary>
        public void Clear()
        {
            lock (gate)
            {
                pending.Clear();
                sessionGeneration++;
            }
        }

        private string MintClaimId()
        {
            var claimId = newClaimId();
            if (string.IsNullOrEmpty(claimId)) throw new InvalidOperationException("haiven: the claim id source returned an empty id.");
            return claimId;
        }

        private void ReleaseSaved((string IntegrationId, string RewardRef) key, string claimId)
        {
            if (!saved.TryGetValue(key, out var current) || current != claimId) return;
            saved.Remove(key);
            savedOrder.Remove(key);
            PersistSaved();
        }

        private void LoadSaved()
        {
            if (savedStore is null) return;
            var document = savedStore.Load();
            if (string.IsNullOrEmpty(document)) return;
            IHaivenJsonNode root;
            try
            {
                root = HaivenJson.Parse(document);
            }
            catch (HaivenJsonException)
            {
                return;
            }
            if (root.Kind != HaivenJsonKind.Array) return;
            foreach (var item in root.EnumerateArray())
            {
                if (item.Kind != HaivenJsonKind.Object) continue;
                if (!item.TryGetProperty("claimId", out var claimNode) || claimNode.Kind != HaivenJsonKind.String) continue;
                if (!item.TryGetProperty("integrationId", out var integrationNode) || integrationNode.Kind != HaivenJsonKind.String) continue;
                if (!item.TryGetProperty("rewardRef", out var rewardNode) || rewardNode.Kind != HaivenJsonKind.String) continue;
                var claimId = claimNode.GetString();
                var integrationId = integrationNode.GetString();
                var rewardRef = rewardNode.GetString();
                if (string.IsNullOrEmpty(claimId) || string.IsNullOrEmpty(integrationId) || string.IsNullOrEmpty(rewardRef)) continue;
                if (UnicodeCodePointCount(rewardRef) > 128) continue;
                var key = (integrationId, rewardRef);
                if (saved.ContainsKey(key)) continue;
                saved[key] = claimId;
                savedOrder.Add(key);
            }
        }

        private void PersistSaved()
        {
            if (savedStore is null) return;
            var rows = new List<Dictionary<string, string>>(savedOrder.Count);
            foreach (var key in savedOrder)
            {
                rows.Add(new Dictionary<string, string>
                {
                    ["claimId"] = saved[key],
                    ["integrationId"] = key.IntegrationId,
                    ["rewardRef"] = key.RewardRef,
                });
            }
            savedStore.Save(HaivenJson.Serialize(rows));
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(
                completed =>
                {
                    if (completed.IsFaulted) _ = completed.Exception;
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static int UnicodeCodePointCount(string value)
        {
            var count = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsHighSurrogate(value[index]) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1])) index++;
                count++;
            }
            return count;
        }

        /// <summary>
        /// A game server's grant of one reward to a player
        /// (<c>POST /integrations/rewards/claims</c>), recorded as
        /// <c>server_confirmed</c>. <paramref name="serverApi"/>'s transport must
        /// send the integration key, which never ships in a game client. Keep
        /// <paramref name="claimId"/> across your own retries.
        /// </summary>
        public static Task<RewardClaimOutcome> GrantAsync(
            RestApi serverApi,
            string userId,
            string rewardRef,
            string claimId,
            CancellationToken cancellationToken = default)
        {
            if (serverApi is null) throw new ArgumentNullException(nameof(serverApi));
            if (string.IsNullOrEmpty(userId)) throw new ArgumentException("A user id is required.", nameof(userId));
            if (string.IsNullOrEmpty(rewardRef)) throw new ArgumentException("A reward ref is required.", nameof(rewardRef));
            if (string.IsNullOrEmpty(claimId)) throw new ArgumentException("A claim id is required.", nameof(claimId));
            var request = new ServerClaimRewardRequest { ClaimId = claimId, RewardRef = rewardRef, UserId = userId };
            return SendAsync(() => serverApi.PostIntegrationsRewardsClaimsAsync(request, cancellationToken), cancellationToken);
        }

        /// <summary>
        /// A game server's confirmation of a claim the player's client made
        /// (<c>POST /integrations/rewards/claims/{claim_id}/confirm</c>), with the
        /// integration key. Confirming twice returns the confirmed claim.
        /// </summary>
        public static Task<RewardClaimOutcome> ConfirmAsync(
            RestApi serverApi,
            string claimId,
            CancellationToken cancellationToken = default)
        {
            if (serverApi is null) throw new ArgumentNullException(nameof(serverApi));
            if (string.IsNullOrEmpty(claimId)) throw new ArgumentException("A claim id is required.", nameof(claimId));
            return SendAsync(() => serverApi.PostIntegrationsRewardsClaimsClaimIdConfirmAsync(claimId, cancellationToken), cancellationToken);
        }

        /// <summary>A UUIDv7 (RFC 9562): 48 bits of Unix milliseconds, the version, 74 random bits, the variant.</summary>
        public static string NewUuidV7()
        {
            var random = new byte[10];
            using (var generator = RandomNumberGenerator.Create()) generator.GetBytes(random);
            return FormatUuidV7(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), random);
        }

        internal static string FormatUuidV7(long unixMilliseconds, byte[] random)
        {
            var bytes = new byte[16];
            for (var index = 0; index < 6; index++) bytes[index] = (byte)((unixMilliseconds >> (8 * (5 - index))) & 0xFF);
            bytes[6] = (byte)(0x70 | (random[0] & 0x0F));
            bytes[7] = random[1];
            bytes[8] = (byte)(0x80 | (random[2] & 0x3F));
            Array.Copy(random, 3, bytes, 9, 7);
            var text = new StringBuilder(36);
            for (var index = 0; index < 16; index++)
            {
                if (index == 4 || index == 6 || index == 8 || index == 10) text.Append('-');
                text.Append(bytes[index].ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return text.ToString();
        }

        internal static RewardConfirmation ConfirmationOf(string? confirmation)
        {
            switch (confirmation)
            {
                case "client_reported": return RewardConfirmation.ClientReported;
                case "server_confirmed": return RewardConfirmation.ServerConfirmed;
                default: return RewardConfirmation.Unrecognized;
            }
        }

        /// <summary>Any reward failure, by status and the error body's finite code.</summary>
        internal static RewardClaimOutcome Classify(Exception error)
        {
            if (!(error is HaivenRestException rest))
                return new RewardClaimOutcome(RewardClaimResult.Failed, isRetryable: true, exception: error);

            var status = rest.StatusCode;
            var code = rest.Code;
            RewardClaimResult result;
            if (status == 403 && code == "reward_not_eligible") result = RewardClaimResult.NotEligible;
            else if (status == 409 && code == "reward_already_claimed") result = RewardClaimResult.AlreadyClaimed;
            else if (status == 409 && code == "reward_claim_conflict") result = RewardClaimResult.ClaimConflict;
            else if (status == 409 && code == "refund_in_progress") result = RewardClaimResult.RefundInProgress;
            else if (status == 404) result = RewardClaimResult.NotFound;
            else
            {
                var retryable = status == 429 || status >= 500;
                return new RewardClaimOutcome(RewardClaimResult.Failed, statusCode: status, errorCode: code, isRetryable: retryable, exception: rest);
            }
            return new RewardClaimOutcome(result, statusCode: status, errorCode: code, exception: rest);
        }

        private static async Task<RewardClaimOutcome> SendAsync(
            Func<Task<RewardClaimResponse?>> send,
            CancellationToken cancellationToken)
        {
            RewardClaimResponse? claim;
            try
            {
                claim = await send().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                return Classify(error);
            }
            // A 2xx that is not a claim may still have been recorded: retry under the same id.
            if (claim is null || string.IsNullOrEmpty(claim.ClaimId))
                return new RewardClaimOutcome(
                    RewardClaimResult.Failed,
                    isRetryable: true,
                    exception: new InvalidOperationException("haiven: the reward reply carried no claim."));
            return new RewardClaimOutcome(RewardClaimResult.Claimed, claim);
        }
    }
}
