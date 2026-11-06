using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk.Tests
{
    internal static class AnalyticsTests
    {
        public static async Task RunAsync(Action<string, bool> check)
        {
            await BatchesAndBoundsDeterministically(check);
            await RetriesTheSameEventIdsWithBoundedBackoff(check);
            await SnapshotsCallerEventBeforeSending(check);
            await KeepsSnapshotDuringRetry(check);
            await DisposalDuringRetryCompletesFlush(check);
            await ReportsPermanentDrops(check);
            await PreservesHostContextAcrossPumpAndRetry(check);
            await SchedulerStartFailureSettlesFlushAndRecovers(check);

            var rejected = false;
            try { _ = new AnalyticsBatcherOptions { BatchSize = 101 }; }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            check("analytics batch sizes are bounded by the endpoint contract", rejected);
        }

        private static async Task BatchesAndBoundsDeterministically(Action<string, bool> check)
        {
            var sent = new List<string[]>();
            var delay = new FirstDelayGate();
            var ids = 0;
            using var batcher = Create(
                request =>
                {
                    sent.Add(request.Events!.Select(item => item.EventId!).ToArray());
                    return Task.FromResult<IngestAnalyticsResponse?>(new IngestAnalyticsResponse());
                },
                delay,
                new AnalyticsBatcherOptions { BatchSize = 2, MaxBufferedEvents = 5 },
                () => $"event-{++ids}");

            check("analytics enqueue returns immediately", batcher.TryEnqueue(Event("launch")));
            await delay.Entered;
            for (var index = 0; index < 4; index++)
                check("analytics accepts events within its memory bound", batcher.TryEnqueue(Event($"event-{index}")));
            check("analytics drops rather than growing beyond its memory bound", !batcher.TryEnqueue(Event("overflow")));

            delay.Release();
            await batcher.FlushAsync();

            check("analytics sends deterministic bounded batches",
                sent.Count == 3 && sent[0].Length == 2 && sent[1].Length == 2 && sent[2].Length == 1);
            check("analytics gives each event one stable id",
                sent.SelectMany(batch => batch).SequenceEqual(new[]
                    { "event-1", "event-2", "event-3", "event-4", "event-5" }));
            check("analytics exposes queue-full drops", batcher.DroppedEventCount == 1);
        }

        private static async Task RetriesTheSameEventIdsWithBoundedBackoff(Action<string, bool> check)
        {
            var attempts = new List<string[]>();
            var delay = new FirstDelayGate();
            var calls = 0;
            using var batcher = Create(
                request =>
                {
                    attempts.Add(request.Events!.Select(item => item.EventId!).ToArray());
                    if (++calls == 1) throw new InvalidOperationException("offline");
                    return Task.FromResult<IngestAnalyticsResponse?>(new IngestAnalyticsResponse());
                },
                delay,
                new AnalyticsBatcherOptions
                {
                    BatchSize = 2,
                    MaxBufferedEvents = 2,
                    MaxRetries = 2,
                    InitialRetryDelay = TimeSpan.FromMilliseconds(25),
                    MaxRetryDelay = TimeSpan.FromMilliseconds(40),
                },
                () => "stable-event-id");

            batcher.TryEnqueue(Event("playtime"));
            await delay.Entered;
            delay.Release();
            await batcher.FlushAsync();

            check("analytics retries a failed request once", attempts.Count == 2 && batcher.RetryCount == 1);
            check("analytics retries with the original event id",
                attempts[0].SequenceEqual(attempts[1]) && attempts[0].Single() == "stable-event-id");
            check("analytics retry delay follows the bounded policy",
                delay.AfterFirst.SequenceEqual(new[] { TimeSpan.FromMilliseconds(25) }));
        }

        private static async Task ReportsPermanentDrops(Action<string, bool> check)
        {
            var delay = new FirstDelayGate();
            var dropped = new TaskCompletionSource<AnalyticsDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var batcher = Create(
                _ => throw new InvalidOperationException("still offline"),
                delay,
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    MaxRetries = 0,
                },
                () => "dropped-event");
            batcher.Diagnostic += diagnostic =>
            {
                if (diagnostic.Kind == AnalyticsDiagnosticKind.BatchDropped) dropped.TrySetResult(diagnostic);
            };

            batcher.TryEnqueue(Event("sdk.health"));
            await delay.Entered;
            delay.Release();
            await batcher.FlushAsync();
            var diagnostic = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(2));

            check("analytics counts events dropped after retry exhaustion", batcher.DroppedEventCount == 1);
            check("analytics reports the failed event id and exception",
                diagnostic.EventIds.SequenceEqual(new[] { "dropped-event" })
                && diagnostic.Exception is InvalidOperationException);
        }

        private static async Task SnapshotsCallerEventBeforeSending(Action<string, bool> check)
        {
            AnalyticsEvent? sent = null;
            var delay = new FirstDelayGate();
            using var batcher = Create(
                request =>
                {
                    sent = request.Events!.Single();
                    return Task.FromResult<IngestAnalyticsResponse?>(new IngestAnalyticsResponse());
                },
                delay,
                new AnalyticsBatcherOptions { BatchSize = 1, MaxBufferedEvents = 1 },
                () => "unused-generated-id");
            var properties = new Dictionary<string, IHaivenJsonNode>
            {
                ["level"] = Number(4),
            };
            var analyticsEvent = new AnalyticsEvent
            {
                AnalyticsSessionId = "caller-session",
                EventId = "caller-event",
                OccurredAt = "2026-10-09T00:00:00Z",
                Properties = properties,
                Type = "level.started",
            };

            batcher.TryEnqueue(analyticsEvent);
            await delay.Entered;
            analyticsEvent.AnalyticsSessionId = "mutated-session";
            analyticsEvent.EventId = "mutated-event";
            analyticsEvent.OccurredAt = "2026-10-10T00:00:00Z";
            analyticsEvent.Type = "mutated.type";
            properties["level"] = Number(99);
            analyticsEvent.Properties = null;
            delay.Release();
            await batcher.FlushAsync();

            check("analytics snapshots caller events before the first send",
                sent?.AnalyticsSessionId == "caller-session"
                && sent.EventId == "caller-event"
                && sent.OccurredAt == "2026-10-09T00:00:00Z"
                && sent.Type == "level.started"
                && sent.Properties?["level"].GetNumberText() == "4");
        }

        private static async Task KeepsSnapshotDuringRetry(Action<string, bool> check)
        {
            var attempts = new List<string>();
            var retryDelay = new FirstDelayGate();
            var analyticsEvent = new AnalyticsEvent
            {
                EventId = "retry-event",
                Type = "match.started",
                Properties = new Dictionary<string, IHaivenJsonNode>
                {
                    ["round"] = Number(1),
                },
            };
            using var batcher = Create(
                request =>
                {
                    var item = request.Events!.Single();
                    attempts.Add($"{item.EventId}:{item.Type}:{item.Properties!["round"].GetNumberText()}");
                    if (attempts.Count == 1) throw new InvalidOperationException("lost reply");
                    return Task.FromResult<IngestAnalyticsResponse?>(new IngestAnalyticsResponse());
                },
                retryDelay,
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    MaxRetries = 1,
                    FlushInterval = TimeSpan.Zero,
                },
                () => "unused-generated-id");

            batcher.TryEnqueue(analyticsEvent);
            await retryDelay.Entered;
            analyticsEvent.EventId = "mutated-retry-event";
            analyticsEvent.Type = "mutated.type";
            analyticsEvent.Properties!["round"] = Number(2);
            retryDelay.Release();
            await batcher.FlushAsync();

            check("analytics keeps the accepted snapshot throughout retries",
                attempts.SequenceEqual(new[] { "retry-event:match.started:1", "retry-event:match.started:1" }));
        }

        private static IHaivenJsonNode Number(int value) =>
            BuiltInJsonCodec.Instance.Parse(value.ToString(CultureInfo.InvariantCulture));

        private static async Task DisposalDuringRetryCompletesFlush(Action<string, bool> check)
        {
            var retryDelay = new FirstDelayGate();
            var batcher = Create(
                _ => throw new InvalidOperationException("offline"),
                retryDelay,
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    MaxRetries = 1,
                    FlushInterval = TimeSpan.Zero,
                },
                () => "dispose-event");

            batcher.TryEnqueue(Event("dispose.during-retry"));
            var flush = batcher.FlushAsync();
            await retryDelay.Entered;
            batcher.Dispose();
            await flush.WaitAsync(TimeSpan.FromSeconds(2));

            check("disposing during retry settles the in-flight batch and flush",
                batcher.BufferedEventCount == 0 && batcher.DroppedEventCount == 1 && flush.IsCompletedSuccessfully);
        }

        private static Task PreservesHostContextAcrossPumpAndRetry(Action<string, bool> check)
        {
            using var hostContext = new PumpSynchronizationContext();
            hostContext.Run(async () =>
            {
                var hostThread = System.Environment.CurrentManagedThreadId;
                var scheduler = new ContextRecordingScheduler(hostContext);
                var transport = new ContextRecordingTransport(hostContext);
                using var batcher = new AnalyticsBatcher(
                    new RestApi(transport),
                    new AnalyticsContext
                    {
                        Environment = "test",
                        IntegrationId = "integration",
                        Platform = "unity",
                        Sdk = new AnalyticsSDK { Name = "haiven-unity", Version = "test" },
                    },
                    new AnalyticsBatcherOptions
                    {
                        BatchSize = 1,
                        MaxBufferedEvents = 1,
                        MaxRetries = 1,
                        FlushInterval = TimeSpan.FromMilliseconds(1),
                        InitialRetryDelay = TimeSpan.FromMilliseconds(1),
                        MaxRetryDelay = TimeSpan.FromMilliseconds(1),
                    },
                    scheduler: scheduler);
                var diagnosticThread = -1;
                batcher.Diagnostic += diagnostic =>
                {
                    if (diagnostic.Kind == AnalyticsDiagnosticKind.RetryScheduled)
                        diagnosticThread = System.Environment.CurrentManagedThreadId;
                };

                check("analytics context probe enqueues an event", batcher.TryEnqueue(Event("context.probe")));
                await batcher.FlushAsync();

                check("analytics preserves the host context across collection and retry delays",
                    scheduler.DelayThreads.Count == 2
                    && scheduler.DelayThreads.All(thread => thread == hostThread));
                check("analytics preserves the host context across asynchronous sends",
                    transport.SendThreads.Count == 2
                    && transport.SendThreads.All(thread => thread == hostThread));
                check("analytics dispatches retry diagnostics on the host context",
                    diagnosticThread == hostThread);
            });
            return Task.CompletedTask;
        }

        private static async Task SchedulerStartFailureSettlesFlushAndRecovers(Action<string, bool> check)
        {
            var scheduler = new FirstStartThrowsScheduler();
            var transport = new RecordingTransport();
            using var batcher = new AnalyticsBatcher(
                new RestApi(transport),
                new AnalyticsContext
                {
                    Environment = "test",
                    IntegrationId = "integration",
                    Platform = "unity",
                    Sdk = new AnalyticsSDK { Name = "haiven-unity", Version = "test" },
                },
                new AnalyticsBatcherOptions
                {
                    BatchSize = 1,
                    MaxBufferedEvents = 1,
                    FlushInterval = TimeSpan.Zero,
                },
                "analytics-session",
                scheduler);
            var dropped = new TaskCompletionSource<AnalyticsDiagnostic>(TaskCreationOptions.RunContinuationsAsynchronously);
            batcher.Diagnostic += diagnostic =>
            {
                if (diagnostic.Kind == AnalyticsDiagnosticKind.BatchDropped) dropped.TrySetResult(diagnostic);
            };
            // A flush requested while the refused Start is in progress must settle too.
            Task? pendingFlush = null;
            scheduler.BeforeThrow = () => pendingFlush = batcher.FlushAsync();

            var threw = false;
            try { batcher.TryEnqueue(new AnalyticsEvent { EventId = "refused-event", Type = "scheduler.destroyed" }); }
            catch (ObjectDisposedException) { threw = true; }
            var flush = batcher.FlushAsync();

            check("analytics rethrows a scheduler that refuses to start", threw);
            check("analytics settles flushes at once when the scheduler refuses to start",
                pendingFlush is { IsCompletedSuccessfully: true } && flush.IsCompletedSuccessfully);
            check("analytics drops the queue a refused scheduler would have sent",
                batcher.BufferedEventCount == 0 && batcher.DroppedEventCount == 1);
            var diagnostic = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check("analytics reports the refused start as a dropped batch",
                diagnostic.EventIds.SequenceEqual(new[] { "refused-event" })
                && diagnostic.Exception is ObjectDisposedException);

            check("analytics accepts events after a refused start", batcher.TryEnqueue(Event("scheduler.recovered")));
            await batcher.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
            check("analytics restarts the drain after a refused start",
                scheduler.Starts == 2
                && transport.Bodies.Count == 1
                && transport.Bodies[0].Contains("scheduler.recovered")
                && !transport.Bodies[0].Contains("scheduler.destroyed"));
        }

        private static AnalyticsBatcher Create(
            Func<IngestAnalyticsRequest, Task<IngestAnalyticsResponse?>> send,
            FirstDelayGate delay,
            AnalyticsBatcherOptions options,
            Func<string> nextId)
        {
            return new AnalyticsBatcher(
                (request, _) => send(request),
                new AnalyticsContext
                {
                    Environment = "test",
                    IntegrationId = "integration",
                    Platform = "unity",
                    Sdk = new AnalyticsSDK { Name = "haiven-unity", Version = "test" },
                },
                options,
                "analytics-session",
                delay.Wait,
                nextId,
                () => DateTimeOffset.Parse("2026-10-09T00:00:00Z"));
        }

        private static AnalyticsEvent Event(string type) => new AnalyticsEvent { Type = type };

        private sealed class ContextRecordingScheduler : IAnalyticsScheduler
        {
            private readonly PumpSynchronizationContext context;

            public ContextRecordingScheduler(PumpSynchronizationContext context)
            {
                this.context = context;
            }

            public List<int> DelayThreads { get; } = new List<int>();

            public void Start(Func<Task> operation)
            {
                context.Post(_ => { _ = operation(); }, null);
            }

            public Task Delay(TimeSpan duration, CancellationToken cancellationToken)
            {
                DelayThreads.Add(System.Environment.CurrentManagedThreadId);
                return context.CompleteOffContext(cancellationToken);
            }

            public void Post(Action callback)
            {
                context.Post(_ => callback(), null);
            }
        }

        private sealed class FirstStartThrowsScheduler : IAnalyticsScheduler
        {
            public Action? BeforeThrow { get; set; }
            public int Starts { get; private set; }

            public void Start(Func<Task> operation)
            {
                if (++Starts == 1)
                {
                    BeforeThrow?.Invoke();
                    throw new ObjectDisposedException("UnityAnalyticsScheduler");
                }
                _ = Task.Run(operation);
            }

            public Task Delay(TimeSpan duration, CancellationToken cancellationToken) =>
                Task.Delay(duration, cancellationToken);

            public void Post(Action callback)
            {
                // Match a destroyed UnityAnalyticsScheduler: it refuses Start and
                // drops later posts, so the refused-start diagnostic cannot depend on Post.
            }
        }

        private sealed class RecordingTransport : IRestApiTransport
        {
            public List<string> Bodies { get; } = new List<string>();

            public Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
            {
                lock (Bodies) Bodies.Add(request.Body ?? string.Empty);
                return Task.FromResult<string?>("{}");
            }

            public Uri Url(string path) => new Uri("http://127.0.0.1" + path);
        }

        private sealed class ContextRecordingTransport : IRestApiTransport
        {
            private readonly PumpSynchronizationContext context;

            public ContextRecordingTransport(PumpSynchronizationContext context)
            {
                this.context = context;
            }

            public List<int> SendThreads { get; } = new List<int>();

            public async Task<string?> SendAsync(RestRequest request, CancellationToken cancellationToken)
            {
                SendThreads.Add(System.Environment.CurrentManagedThreadId);
                await context.CompleteOffContext(cancellationToken).ConfigureAwait(false);
                if (SendThreads.Count == 1) throw new InvalidOperationException("simulated lost reply");
                return "{}";
            }

            public Uri Url(string path) => new Uri("http://127.0.0.1" + path);
        }

        private sealed class PumpSynchronizationContext : SynchronizationContext, IDisposable
        {
            private readonly Queue<(SendOrPostCallback Callback, object? State)> callbacks =
                new Queue<(SendOrPostCallback Callback, object? State)>();
            private readonly AutoResetEvent callbackReady = new AutoResetEvent(false);

            public override void Post(SendOrPostCallback callback, object? state)
            {
                lock (callbacks) callbacks.Enqueue((callback, state));
                callbackReady.Set();
            }

            public void Run(Func<Task> operation)
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    var task = operation();
                    while (!task.IsCompleted)
                    {
                        if (RunOne()) continue;
                        if (!callbackReady.WaitOne(TimeSpan.FromSeconds(5)))
                            throw new TimeoutException("Timed out waiting for work on the analytics host context.");
                    }
                    while (RunOne()) { }
                    task.GetAwaiter().GetResult();
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }

            public void Dispose() => callbackReady.Dispose();

            public Task CompleteOffContext(CancellationToken cancellationToken)
            {
                var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Post(_ =>
                {
                    ThreadPool.QueueUserWorkItem(__ =>
                    {
                        if (cancellationToken.IsCancellationRequested)
                            completion.TrySetCanceled(cancellationToken);
                        else
                            completion.TrySetResult(true);
                    });
                }, null);
                return completion.Task;
            }

            private bool RunOne()
            {
                (SendOrPostCallback Callback, object? State) work;
                lock (callbacks)
                {
                    if (callbacks.Count == 0) return false;
                    work = callbacks.Dequeue();
                }
                work.Callback(work.State);
                return true;
            }
        }

        private sealed class FirstDelayGate
        {
            private readonly TaskCompletionSource<bool> entered =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private readonly TaskCompletionSource<bool> released =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            private int calls;

            public Task Entered => entered.Task;
            public List<TimeSpan> AfterFirst { get; } = new List<TimeSpan>();

            public Task Wait(TimeSpan duration, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.TrySetResult(true);
                    cancellationToken.Register(() => released.TrySetCanceled());
                    return released.Task;
                }
                AfterFirst.Add(duration);
                return Task.CompletedTask;
            }

            public void Release() => released.TrySetResult(true);
        }
    }
}
