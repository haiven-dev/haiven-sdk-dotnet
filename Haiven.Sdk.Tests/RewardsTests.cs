using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk.Tests
{
    // Does the reward client claim once, however often it is retried? What this
    // pins: a retry after a lost or failed reply reuses the claim id, a final
    // answer releases it, each documented refusal maps to its finite result, an
    // unknown confirmation is never server-confirmed, the server grant and
    // confirm routes, cancellation, and the UUIDv7 layout.
    internal static class RewardsTests
    {
        public static async Task RunAsync(Action<string, bool> check)
        {
            await PhaseOneAccessPreservesTransportSemantics(check);
            await RetriesReuseTheClaimId(check);
            await ClearInvalidatesOutstandingClaims(check);
            await CallbackClearInvalidatesCompletion(check);
            await RewardRefsCountUnicodeCodePoints(check);
            await RefusalsAreFinite(check);
            await ServerGrantAndConfirm(check);
            await CancellationKeepsTheClaimId(check);
            await SavedClaimsWaitForTheServer(check);

            check("a UUIDv7 carries its time, version and variant",
                RewardClaims.FormatUuidV7(0x0192F3A4B5C6L, new byte[] { 0xFF, 0x12, 0xFF, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0 })
                    == "0192f3a4-b5c6-7f12-bf34-56789abcdef0");
            var minted = RewardClaims.NewUuidV7();
            check("the default claim id is a UUIDv7", minted.Length == 36 && minted[14] == '7');
            check("an unknown confirmation is never server-confirmed",
                RewardClaims.ConfirmationOf("notarised") == RewardConfirmation.Unrecognized);
        }

        private static async Task RetriesReuseTheClaimId(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var ids = 0;
            var rewards = new RewardClaims(new RestApi(transport), () => $"claim-{++ids}");
            var claimed = new List<RewardClaimOutcome>();
            rewards.Claimed += claimed.Add;

            transport.Next(_ => throw new HttpRequestException("connection reset"));
            var lost = await rewards.ClaimAsync("game-1", "hat");
            check("a transport failure is a retryable failure", lost.Result == RewardClaimResult.Failed && lost.IsRetryable);
            check("it posts the claim to /users/me/rewards/claims",
                transport.Sent[0].Method == "POST" && transport.Sent[0].Path == "/users/me/rewards/claims"
                && transport.Sent[0].Body!.Contains("\"claimId\":\"claim-1\"") && transport.Sent[0].Body!.Contains("\"integrationId\":\"game-1\""));
            check("the claim id stays pending", rewards.PendingClaimId("game-1", "hat") == "claim-1");

            transport.Next(_ => throw new HaivenRestException(503, null));
            var down = await rewards.ClaimAsync("game-1", "hat");
            check("a server error is retryable and reuses the claim id",
                down.IsRetryable && transport.Sent[1].Body!.Contains("\"claimId\":\"claim-1\""));

            transport.Next(_ => "not json");
            var garbled = await rewards.ClaimAsync("game-1", "hat");
            check("a 2xx that does not decode is retried under the same id",
                garbled.Result == RewardClaimResult.Failed && garbled.IsRetryable && rewards.PendingClaimId("game-1", "hat") == "claim-1");

            transport.Next(_ => Recorded("claim-1", "client_reported"));
            var recorded = await rewards.ClaimAsync("game-1", "hat");
            check("the retry records the claim", recorded.Succeeded && recorded.Claim?.ClaimId == "claim-1");
            check("as reported by the client only", recorded.Confirmation == RewardConfirmation.ClientReported);
            check("a final answer releases the claim id", rewards.PendingClaimId("game-1", "hat") is null);
            check("and Claimed fires once for it", claimed.Count == 1 && claimed[0] == recorded);

            transport.Next(_ => Recorded("claim-2", "server_confirmed"));
            var next = await rewards.ClaimAsync("game-1", "hat");
            check("the next claim of the reward mints a new id",
                transport.Sent[4].Body!.Contains("\"claimId\":\"claim-2\"") && next.Confirmation == RewardConfirmation.ServerConfirmed);

            transport.Next(_ => throw new HttpRequestException("offline"));
            await rewards.ClaimAsync("game-1", "cape");
            rewards.Clear();
            check("Clear forgets every pending claim id", rewards.PendingClaimId("game-1", "cape") is null);
        }

        private static async Task SavedClaimsWaitForTheServer(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var ids = 0;
            var store = new MemoryRewardClaimIntentStore();
            var rewards = new RewardClaims(new RestApi(transport), () => $"saved-{++ids}", store);
            var claimed = new List<RewardClaimOutcome>();
            var settled = new List<RewardClaimOutcome>();
            rewards.Claimed += claimed.Add;
            rewards.ClaimSettled += settled.Add;

            var saved = rewards.Save("game-1", "hat");
            check("saving before link mints one id and does not post", saved.ClaimId == "saved-1" && transport.Sent.Count == 0 && claimed.Count == 0);
            check("saving again reuses the id", rewards.Save("game-1", "hat").ClaimId == "saved-1" && ids == 1);
            check("a saved claim is not pending", rewards.PendingClaimId("game-1", "hat") is null && rewards.SavedClaims().Count == 1);
            check("the store keeps the claim id", store.Document.Contains("\"claimId\":\"saved-1\"") && store.Document.Contains("\"rewardRef\":\"hat\""));

            var restarted = new RewardClaims(new RestApi(transport), () => $"other-{++ids}", store);
            check("a restarted client reloads the saved claim id",
                restarted.SavedClaims().Count == 1 && restarted.SavedClaims()[0].ClaimId == "saved-1" && ids == 1);

            rewards.Clear();
            check("sign-out keeps the saved claim", rewards.SavedClaims().Count == 1 && rewards.PendingClaimId("game-1", "hat") is null);

            transport.Next(_ => throw new HttpRequestException("offline"));
            var lost = (await rewards.SubmitSavedAsync())[0];
            check("a lost submit is not loot and keeps the saved id",
                lost.Result == RewardClaimResult.Failed && lost.IsRetryable && !lost.Succeeded
                && rewards.SavedClaims()[0].ClaimId == "saved-1" && claimed.Count == 0 && settled.Count == 1);

            transport.Next(_ => throw new HaivenRestException(403, "{\"code\":\"reward_not_eligible\"}"));
            var refused = (await rewards.SubmitSavedAsync())[0];
            check("a refusal is surfaced and releases the saved id",
                refused.Result == RewardClaimResult.NotEligible && refused.ErrorCode == "reward_not_eligible" && !refused.Succeeded
                && rewards.SavedClaims().Count == 0 && claimed.Count == 0);

            var cape = rewards.Save("game-1", "cape");
            transport.Next(_ => Recorded(cape.ClaimId, "client_reported"));
            var recorded = (await rewards.SubmitSavedAsync())[0];
            check("loot waits until the server records the saved claim",
                recorded.Succeeded && recorded.Claim?.ClaimId == cape.ClaimId && claimed.Count == 1 && rewards.SavedClaims().Count == 0
                && transport.Sent[transport.Sent.Count - 1].Path == "/users/me/rewards/claims"
                && transport.Sent[transport.Sent.Count - 1].Body!.Contains("\"claimId\":\"" + cape.ClaimId + "\""));

            var quoted = new MemoryRewardClaimIntentStore();
            var quoting = new RewardClaims(new RestApi(transport), () => "quoted-1", quoted);
            quoting.Save("game-1", "hat\"cape");
            var reloaded = new RewardClaims(new RestApi(transport), () => "nope", quoted);
            check("a reward ref survives the saved document", reloaded.SavedClaims()[0].RewardRef == "hat\"cape" && reloaded.SavedClaims()[0].ClaimId == "quoted-1");
        }

        private static async Task ClearInvalidatesOutstandingClaims(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var ids = 0;
            var rewards = new RewardClaims(new RestApi(transport), () => $"claim-{++ids}");
            var claimed = new List<RewardClaimOutcome>();
            rewards.Claimed += claimed.Add;
            var delayed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

            transport.NextAsync(_ => delayed.Task);
            var playerA = rewards.ClaimAsync("game-1", "scarf");
            var playerAClaimId = rewards.PendingClaimId("game-1", "scarf");
            rewards.Clear();
            transport.Next(_ => throw new HttpRequestException("offline"));
            await rewards.ClaimAsync("game-1", "scarf");
            var playerBClaimId = rewards.PendingClaimId("game-1", "scarf");

            delayed.SetResult(Recorded(playerAClaimId!, "client_reported"));
            var invalidated = false;
            try { await playerA; }
            catch (OperationCanceledException) { invalidated = true; }
            check("a completion from before Clear is invalidated without raising Claimed", invalidated && claimed.Count == 0);
            check("the next player's pending claim id survives that stale completion",
                playerBClaimId != playerAClaimId && rewards.PendingClaimId("game-1", "scarf") == playerBClaimId);
        }

        private static async Task CallbackClearInvalidatesCompletion(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var ids = 0;
            var rewards = new RewardClaims(new RestApi(transport), () => $"claim-{++ids}");
            Task<RewardClaimOutcome>? playerB = null;
            var deliveredAfterClear = 0;

            rewards.Claimed += _ =>
            {
                rewards.Clear();
                transport.Next(_ => throw new HttpRequestException("offline"));
                playerB = rewards.ClaimAsync("game-1", "scarf");
            };
            rewards.Claimed += _ => deliveredAfterClear++;
            transport.Next(_ => Recorded("claim-1", "client_reported"));

            var invalidated = false;
            try { await rewards.ClaimAsync("game-1", "scarf"); }
            catch (OperationCanceledException) { invalidated = true; }
            if (playerB is not null) await playerB;

            check("Clear from Claimed cancels the old player's returned outcome", invalidated);
            check("Clear from Claimed stops delivery to later handlers", deliveredAfterClear == 0);
            check("the callback-started player's pending claim survives the old completion",
                playerB is not null && rewards.PendingClaimId("game-1", "scarf") == "claim-2");
        }

        private static async Task RewardRefsCountUnicodeCodePoints(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var rewards = new RewardClaims(new RestApi(transport));
            var supplementary = char.ConvertFromUtf32(0x1F3A9);

            transport.Next(_ => Recorded("ascii-128", "client_reported"));
            await rewards.ClaimAsync("game-1", new string('x', 128));
            check("a 128-code-point ASCII reward ref is accepted", transport.Sent.Count == 1);
            var refused = false;
            try { await rewards.ClaimAsync("game-1", new string('x', 129)); }
            catch (ArgumentException) { refused = true; }
            check("a 129-code-point ASCII reward ref is refused before sending", refused && transport.Sent.Count == 1);

            transport.Next(_ => Recorded("supplementary-128", "client_reported"));
            await rewards.ClaimAsync("game-1", string.Concat(System.Linq.Enumerable.Repeat(supplementary, 128)));
            check("a 128-code-point supplementary reward ref is accepted", transport.Sent.Count == 2);
            refused = false;
            try { await rewards.ClaimAsync("game-1", string.Concat(System.Linq.Enumerable.Repeat(supplementary, 129))); }
            catch (ArgumentException) { refused = true; }
            check("a 129-code-point supplementary reward ref is refused before sending", refused && transport.Sent.Count == 2);
        }

        private static async Task RefusalsAreFinite(Action<string, bool> check)
        {
            var cases = new (int Status, string Code, RewardClaimResult Result)[]
            {
                (403, "reward_not_eligible", RewardClaimResult.NotEligible),
                (409, "reward_already_claimed", RewardClaimResult.AlreadyClaimed),
                (409, "reward_claim_conflict", RewardClaimResult.ClaimConflict),
                (409, "refund_in_progress", RewardClaimResult.RefundInProgress),
            };
            var transport = new ScriptedTransport();
            var rewards = new RewardClaims(new RestApi(transport));
            foreach (var (status, code, result) in cases)
            {
                transport.Next(_ => throw new HaivenRestException(status, Refusal(status, code)));
                var outcome = await rewards.ClaimAsync("game-1", "boots");
                check($"{code} is {result}, final, with its code",
                    outcome.Result == result && outcome.ErrorCode == code && outcome.StatusCode == status && !outcome.IsRetryable && !outcome.Succeeded);
                check($"{code} releases the claim id", rewards.PendingClaimId("game-1", "boots") is null);
            }

            transport.Next(_ => throw new HaivenRestException(409, Refusal(409, "resource_already_exists")));
            var unknown = await rewards.ClaimAsync("game-1", "boots");
            check("an unlisted code is a final failure that keeps its code",
                unknown.Result == RewardClaimResult.Failed && unknown.ErrorCode == "resource_already_exists" && !unknown.IsRetryable);
            check("a non-JSON error body has no code", new HaivenRestException(500, "<html>").Code is null);
        }

        private static async Task ServerGrantAndConfirm(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var server = new RestApi(transport);

            transport.Next(_ => Recorded("g-1", "server_confirmed"));
            var granted = await RewardClaims.GrantAsync(server, "u-1", "hat", "g-1");
            check("a grant posts to /integrations/rewards/claims and is server-confirmed",
                transport.Sent[0].Path == "/integrations/rewards/claims" && transport.Sent[0].Body!.Contains("\"userId\":\"u-1\"")
                && granted.Succeeded && granted.Confirmation == RewardConfirmation.ServerConfirmed);

            transport.Next(_ => Recorded("claim 1", "server_confirmed"));
            var confirmed = await RewardClaims.ConfirmAsync(server, "claim 1");
            check("a confirmation encodes the claim id into its path",
                transport.Sent[1].Path == "/integrations/rewards/claims/claim%201/confirm" && confirmed.Succeeded);

            transport.Next(_ => throw new HaivenRestException(404, Refusal(404, "not_found")));
            var missing = await RewardClaims.ConfirmAsync(server, "other");
            check("confirming another integration's claim is not found", missing.Result == RewardClaimResult.NotFound && !missing.IsRetryable);
        }

        private static async Task CancellationKeepsTheClaimId(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var rewards = new RewardClaims(new RestApi(transport), () => "claim-c");
            using var cancel = new CancellationTokenSource();
            transport.Next(token =>
            {
                cancel.Cancel();
                token.ThrowIfCancellationRequested();
                return null;
            });
            var threw = false;
            try { await rewards.ClaimAsync("game-1", "hat", cancel.Token); }
            catch (OperationCanceledException) { threw = true; }
            check("cancelling a claim throws", threw);
            check("and keeps its claim id, since it may have been recorded", rewards.PendingClaimId("game-1", "hat") == "claim-c");
        }

        private static async Task PhaseOneAccessPreservesTransportSemantics(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var api = new RestApi(transport);

            transport.Next(_ => "{\"access\":true,\"basis\":\"free\",\"permanent\":true,\"refundHold\":false}");
            var access = await PlayerAccess.GetGameAsync(api, 42);
            check("game access delegates to the generated route",
                access?.Access == true && transport.Sent[0].Method == "GET" && transport.Sent[0].Path == "/games/42/access");

            transport.Next(_ => null);
            check("a successful no-body game access reply remains null", await PlayerAccess.GetGameAsync(api, 42) is null);

            var openFailure = new HaivenRestException(503, Refusal(503, "auth_backend_changed"));
            transport.Next(_ => throw openFailure);
            Exception? observed = null;
            try { await PlayerAccess.GetGameAsync(api, 42); }
            catch (Exception error) { observed = error; }
            check("game access preserves an open REST error unchanged",
                ReferenceEquals(openFailure, observed) && openFailure.Code == "auth_backend_changed");

            var eligibility = new RewardEligibilityResponse
            {
                Eligible = false,
                Statuses = new List<PlayerStatus>
                {
                    new PlayerStatus { Code = "founder", Tier = "gold" },
                    new PlayerStatus { Code = "recognised_later", Tier = "one" },
                },
            };
            check("founder status is independent of subscriber eligibility",
                PlayerAccess.FounderStatus(eligibility)?.Tier == "gold" && eligibility.Eligible == false);
            check("unknown player status codes remain findable verbatim",
                PlayerAccess.FindStatus(eligibility, "recognised_later")?.Code == "recognised_later");
        }

        private static string Recorded(string claimId, string confirmation) =>
            "{\"claimId\":\"" + claimId + "\",\"claimedAt\":\"2026-10-10T00:00:00Z\",\"confirmation\":\"" + confirmation
            + "\",\"integrationId\":\"game-1\",\"rewardRef\":\"hat\"}";

        private static string Refusal(int status, string code) =>
            "{\"code\":\"" + code + "\",\"message\":\"no\",\"status\":" + status + "}";

        private sealed class ScriptedTransport : IRestApiTransport
        {
            private readonly Queue<Func<CancellationToken, Task<string?>>> replies = new Queue<Func<CancellationToken, Task<string?>>>();

            public List<RestRequest> Sent { get; } = new List<RestRequest>();

            public void Next(Func<CancellationToken, string?> reply) => replies.Enqueue(token => Task.FromResult(reply(token)));

            public void NextAsync(Func<CancellationToken, Task<string?>> reply) => replies.Enqueue(reply);

            public Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
            {
                Sent.Add(request);
                return replies.Dequeue()(cancellationToken);
            }

            public Uri Url(string path) => new Uri("https://api.example.test" + path);
        }
    }
}
