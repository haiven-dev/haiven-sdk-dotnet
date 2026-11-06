using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk.Tests
{
    internal static class IdentityTests
    {
        public static async Task RunAsync(Action<string, bool> check)
        {
            var transport = new ScriptedTransport();
            var browser = new BrowserContext();
            using var identity = new HaivenIdentity(new RestApi(transport), "partner game", browser);
            var motdChanges = new List<HaivenMotd?>();
            var methodChanges = new List<HaivenSignInMethods?>();
            var linked = new List<HaivenIdentityMethod>();
            var failures = new List<IdentityLinkFailure>();
            var diagnostics = new List<string>();
            identity.MotdChanged += motdChanges.Add;
            identity.SignInMethodsChanged += methodChanges.Add;
            identity.IdentityLinked += linked.Add;
            identity.IdentityLinkFailed += failures.Add;
            identity.Diagnostic += diagnostics.Add;

            transport.Next(Motd("00000000-0000-4000-8000-000000000001", "Welcome"));
            var motd = await identity.LoadMotdAsync();
            check("identity facade reads the public MOTD with the integration id",
                transport.Sent[0].Method == "GET"
                && transport.Sent[0].Path == "/motd?integration_id=partner%20game"
                && motd?.Body == "Welcome");
            check("MOTD output is normalized and safe for display",
                motd?.Id == "00000000-0000-4000-8000-000000000001"
                && motd.Url?.AbsoluteUri == "https://news.example.test/update"
                && motdChanges.Single() == motd);

            transport.Next("{\"hasPassword\":true,\"identities\":[{\"provider\":\"google\",\"email\":\"one@example.test\"}]}");
            var methods = await identity.LoadSignInMethodsAsync();
            check("identity facade reads sign-in methods through generated REST",
                transport.Sent[1].Path == "/users/me/sign-in-methods"
                && methods.HasPassword
                && methods.Identities.Single().Provider == "google");

            var restCount = transport.Sent.Count;
            var launch = await identity.BeginLinkAsync(" APPLE ");
            check("link initiation belongs to the injected same-browser context",
                launch.Succeeded
                && browser.Providers.SequenceEqual(new[] { "apple" })
                && browser.Steps.SequenceEqual(new[] { "popup", "same_browser_post", "same_popup_redirect" }));
            check("the facade never starts linking through the game REST transport",
                transport.Sent.Count == restCount);

            var router = new WsEventRouter();
            identity.Attach(router);
            check("the generated realtime router delivers a link completion",
                router.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.linked\",\"payload\":{\"provider\":\"apple\",\"email\":\"two@example.test\"}}")));
            check("link completion updates facade state and reaches the UI event",
                linked.Count == 1
                && linked[0].Provider == "apple"
                && identity.CurrentSignInMethods?.Identities.Count == 2);

            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"account.identity.link.failed\",\"payload\":{\"provider\":\"google\",\"code\":\"identity_already_linked\"}}"));
            check("a link refusal preserves the provider and finite error code",
                failures.Count == 1
                && failures[0].Provider == "google"
                && failures[0].Code == "identity_already_linked");

            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"account.identity.link.failed\",\"payload\":{\"provider\":\"google\",\"code\":\"Bad Code\"}}"));
            check("a malformed link refusal is diagnostic rather than UI state",
                failures.Count == 1 && diagnostics.Contains("invalid_link_failure_event"));

            transport.Next(null);
            await identity.UnlinkAsync("apple");
            check("unlinking uses authenticated generated REST and updates state",
                transport.Sent.Last().Method == "DELETE"
                && transport.Sent.Last().Path == "/users/me/sign-in-methods/sso/apple"
                && identity.CurrentSignInMethods?.Identities.All(value => value.Provider != "apple") == true);

            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"motd\",\"payload\":{\"id\":\"00000000-0000-4000-8000-000000000002\",\"body\":\"Realtime\",\"startsAt\":\"2026-10-14T00:00:00Z\"}}"));
            check("a signed-in realtime MOTD replaces public REST state",
                identity.CurrentMotd?.Body == "Realtime" && motdChanges.Count == 2);

            var delayed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            transport.NextAsync(_ => delayed.Task);
            var stale = identity.LoadMotdAsync();
            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"motd\",\"payload\":{\"id\":\"00000000-0000-4000-8000-000000000003\",\"body\":\"Newer\",\"startsAt\":\"2026-10-14T00:00:00Z\"}}"));
            delayed.SetResult(Motd("00000000-0000-4000-8000-000000000004", "Stale"));
            check("realtime state supersedes an older REST read",
                await FailsWith(stale, "superseded") && identity.CurrentMotd?.Body == "Newer");

            var changesBeforeDuplicate = motdChanges.Count;
            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"motd\",\"payload\":{\"id\":\"00000000-0000-4000-8000-000000000003\",\"body\":\"Duplicate\",\"startsAt\":\"2026-10-14T00:00:00Z\"}}"));
            check("a duplicate current MOTD id does not advance observable state",
                identity.CurrentMotd?.Body == "Newer" && motdChanges.Count == changesBeforeDuplicate);

            identity.DismissMotd();
            var changesAfterDismiss = motdChanges.Count;
            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"motd\",\"payload\":{\"id\":\"00000000-0000-4000-8000-000000000003\",\"body\":\"Still dismissed\",\"startsAt\":\"2026-10-14T00:00:00Z\"}}"));
            check("dismissal suppresses the same MOTD id for the session",
                identity.CurrentMotd is null && motdChanges.Count == changesAfterDismiss);
            router.Dispatch(ServerFrame.Parse(
                "{\"type\":\"motd\",\"payload\":{\"id\":\"00000000-0000-4000-8000-000000000005\",\"body\":\"Next\",\"startsAt\":\"2026-10-14T00:00:00Z\"}}"));
            check("a new MOTD id is visible after dismissing an older one",
                identity.CurrentMotd?.Body == "Next" && motdChanges.Count == changesAfterDismiss + 1);
            check("compare-and-dismiss refuses an older rendered MOTD id",
                !identity.DismissMotd("00000000-0000-4000-8000-000000000003")
                && identity.CurrentMotd?.Body == "Next");
            check("compare-and-dismiss accepts the current MOTD id",
                identity.DismissMotd("00000000-0000-4000-8000-000000000005")
                && identity.CurrentMotd is null);

            using (var dispatchEntered = new ManualResetEventSlim())
            using (var releaseDispatch = new ManualResetEventSlim())
            {
                var inFlightRouter = new WsEventRouter();
                inFlightRouter.On(WsTypes.AccountIdentityLinkFailed, _ =>
                {
                    dispatchEntered.Set();
                    releaseDispatch.Wait();
                });
                identity.Attach(inFlightRouter);
                var diagnosticCount = diagnostics.Count;
                var inFlight = Task.Run(() => inFlightRouter.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.link.failed\",\"payload\":{"
                    + "\"provider\":\"google\",\"code\":\"Bad Code\"}}")));
                var capturedBeforeRetirement = dispatchEntered.Wait(TimeSpan.FromSeconds(10));
                identity.ClearAccount();
                releaseDispatch.Set();
                await inFlight;
                check("account replacement atomically retires in-flight realtime handlers",
                    capturedBeforeRetirement
                    && identity.CurrentSignInMethods is null
                    && methodChanges.Last() is null
                    && diagnostics.Count == diagnosticCount);
            }

            using (var olderReserved = new ManualResetEventSlim())
            using (var releaseOlder = new ManualResetEventSlim())
            using (var orderedIdentity = new HaivenIdentity(new RestApi(transport), "game"))
            {
                var olderRouter = new WsEventRouter();
                var newerRouter = new WsEventRouter();
                var orderedLinked = new List<HaivenIdentityMethod>();
                orderedIdentity.IdentityLinked += orderedLinked.Add;
                HaivenIdentityException? olderFailure = null;
                var olderAttach = Task.Run(() =>
                {
                    try
                    {
                        orderedIdentity.Attach(olderRouter, () =>
                        {
                            olderReserved.Set();
                            if (!releaseOlder.Wait(TimeSpan.FromSeconds(10)))
                                throw new TimeoutException("newer attachment did not release the older one");
                        });
                    }
                    catch (HaivenIdentityException error)
                    {
                        olderFailure = error;
                    }
                });

                var coordinated = olderReserved.Wait(TimeSpan.FromSeconds(10));
                try { orderedIdentity.Attach(newerRouter); }
                finally { releaseOlder.Set(); }
                await olderAttach;
                var newerHandled = newerRouter.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.linked\",\"payload\":{"
                    + "\"provider\":\"google\",\"email\":\"newer@example.test\"}}"));
                var olderHandled = olderRouter.Dispatch(ServerFrame.Parse(
                    "{\"type\":\"account.identity.linked\",\"payload\":{"
                    + "\"provider\":\"apple\",\"email\":\"older@example.test\"}}"));
                check("the newest overlapping router attachment wins deterministically",
                    coordinated
                    && olderFailure?.Code == "superseded"
                    && newerHandled
                    && !olderHandled
                    && orderedLinked.Count == 1
                    && orderedLinked[0].Email == "newer@example.test");
            }

            using var noBrowser = new HaivenIdentity(new RestApi(transport), "game");
            check("linking fails closed without a concrete browser-context adapter",
                await FailsWith(noBrowser.BeginLinkAsync("google"), "browser_context_required"));

            transport.Next("{\"id\":\"00000000-0000-4000-8000-000000000006\"," +
                "\"body\":\"Safe copy\",\"url\":\"https://user@example.test/private\"," +
                "\"startsAt\":\"2026-10-14T00:00:00Z\"}");
            var safeCopy = await noBrowser.LoadMotdAsync();
            check("unsafe MOTD actions are omitted without dropping safe copy",
                safeCopy?.Body == "Safe copy" && safeCopy.Url is null);

            transport.Next("{\"id\":\"not-a-uuid\",\"body\":\"Unsafe\",\"startsAt\":\"2026-10-14T00:00:00Z\"}");
            check("malformed MOTD state fails closed before reaching the UI",
                await FailsWith(noBrowser.LoadMotdAsync(), "invalid_motd"));

            transport.Next("{\"id\":\"00000000-0000-4000-8000-000000000005\","
                + "\"body\":\"Backwards\",\"startsAt\":\"2026-10-14T00:00:00Z\","
                + "\"endsAt\":\"2026-10-13T00:00:00Z\"}");
            check("a MOTD cannot end before it starts",
                await FailsWith(noBrowser.LoadMotdAsync(), "invalid_motd"));

            transport.Next("{\"hasPassword\":false,\"identities\":["
                + "{\"provider\":\"google\",\"email\":\"one@example.test\"},"
                + "{\"provider\":\"google\",\"email\":\"two@example.test\"}]}");
            check("ambiguous duplicate provider state fails closed",
                await FailsWith(noBrowser.LoadSignInMethodsAsync(), "invalid_sign_in_methods"));
        }

        private static async Task<bool> FailsWith(Task task, string code)
        {
            try { await task; }
            catch (HaivenIdentityException error) { return error.Code == code; }
            return false;
        }

        private static string Motd(string id, string body) =>
            "{\"id\":\"" + id + "\",\"title\":\"Update\",\"body\":\"" + body
            + "\",\"url\":\"https://news.example.test/update\",\"startsAt\":\"2026-10-14T00:00:00Z\"}";

        private sealed class BrowserContext : IIdentityLinkBrowserContext
        {
            public List<string> Providers { get; } = new List<string>();
            public List<string> Steps { get; } = new List<string>();

            public Task<IdentityLinkLaunch> BeginLinkAsync(
                string provider,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Providers.Add(provider);
                Steps.Add("popup");
                Steps.Add("same_browser_post");
                Steps.Add("same_popup_redirect");
                return Task.FromResult(IdentityLinkLaunch.Opened());
            }
        }

        private sealed class ScriptedTransport : IRestApiTransport
        {
            private readonly Queue<Func<CancellationToken, Task<string?>>> replies =
                new Queue<Func<CancellationToken, Task<string?>>>();

            public List<RestRequest> Sent { get; } = new List<RestRequest>();

            public void Next(string? reply) =>
                replies.Enqueue(token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(reply);
                });

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
