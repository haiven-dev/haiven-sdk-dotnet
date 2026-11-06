using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Json;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk
{
    /// <summary>
    /// Schedules analytics work for the host runtime. Unity supplies a coroutine-backed
    /// implementation for WebGL; ordinary .NET hosts use the built-in task scheduler.
    /// </summary>
    public interface IAnalyticsScheduler
    {
        /// <summary>Runs <paramref name="operation"/> later. Throwing means it was not scheduled.</summary>
        void Start(Func<Task> operation);
        Task Delay(TimeSpan duration, CancellationToken cancellationToken);
        void Post(Action callback);
    }

    /// <summary>Bounds and retry policy for <see cref="AnalyticsBatcher"/>.</summary>
    public sealed class AnalyticsBatcherOptions
    {
        private int batchSize = 100;
        private int maxBufferedEvents = 1000;
        private int maxRetries = 3;
        private TimeSpan flushInterval = TimeSpan.FromSeconds(1);
        private TimeSpan initialRetryDelay = TimeSpan.FromSeconds(1);
        private TimeSpan maxRetryDelay = TimeSpan.FromSeconds(30);

        /// <summary>Events per request. The analytics endpoint accepts 1 through 100.</summary>
        public int BatchSize
        {
            get => batchSize;
            set => batchSize = value >= 1 && value <= 100
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value), "Batch size must be between 1 and 100.");
        }

        /// <summary>Maximum queued plus in-flight events retained in memory.</summary>
        public int MaxBufferedEvents
        {
            get => maxBufferedEvents;
            set => maxBufferedEvents = value >= 1
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Retries after the first send attempt.</summary>
        public int MaxRetries
        {
            get => maxRetries;
            set => maxRetries = value >= 0
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        /// <summary>Collection window before a newly started drain sends its first batch.</summary>
        public TimeSpan FlushInterval
        {
            get => flushInterval;
            set => flushInterval = value >= TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public TimeSpan InitialRetryDelay
        {
            get => initialRetryDelay;
            set => initialRetryDelay = value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        public TimeSpan MaxRetryDelay
        {
            get => maxRetryDelay;
            set => maxRetryDelay = value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        internal void Validate()
        {
            if (MaxBufferedEvents < BatchSize)
                throw new ArgumentException("MaxBufferedEvents must be at least BatchSize.");
            if (MaxRetryDelay < InitialRetryDelay)
                throw new ArgumentException("MaxRetryDelay must be at least InitialRetryDelay.");
        }
    }

    public enum AnalyticsDiagnosticKind
    {
        QueueFull,
        RetryScheduled,
        BatchDropped,
        EventRejected,
    }

    /// <summary>A non-fatal observation from the background analytics pipeline.</summary>
    public sealed class AnalyticsDiagnostic
    {
        internal AnalyticsDiagnostic(
            AnalyticsDiagnosticKind kind,
            IReadOnlyList<string> eventIds,
            int attempt = 0,
            TimeSpan? delay = null,
            Exception? exception = null,
            string? reason = null)
        {
            Kind = kind;
            EventIds = eventIds;
            Attempt = attempt;
            Delay = delay;
            Exception = exception;
            Reason = reason;
        }

        public AnalyticsDiagnosticKind Kind { get; }
        public IReadOnlyList<string> EventIds { get; }
        public int Attempt { get; }
        public TimeSpan? Delay { get; }
        public Exception? Exception { get; }
        public string? Reason { get; }
    }

    /// <summary>
    /// Unity-safe analytics queue over the shared .NET REST client. Enqueue is bounded and
    /// synchronous; the host scheduler drives HTTP, retries, and diagnostics asynchronously.
    /// </summary>
    public sealed class AnalyticsBatcher : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<AnalyticsEvent> queue = new Queue<AnalyticsEvent>();
        private readonly Func<IngestAnalyticsRequest, CancellationToken, Task<IngestAnalyticsResponse?>> send;
        private readonly IAnalyticsScheduler scheduler;
        private readonly Func<string> nextEventId;
        private readonly Func<DateTimeOffset> utcNow;
        private readonly AnalyticsContext context;
        private readonly AnalyticsBatcherOptions options;
        private readonly string analyticsSessionId;
        private readonly CancellationTokenSource stopped = new CancellationTokenSource();
        private TaskCompletionSource<bool> idle = CompletedSignal();
        private bool pumping;
        private bool disposed;
        private int bufferedEvents;
        private long droppedEvents;
        private long retryCount;

        public AnalyticsBatcher(
            RestApi api,
            AnalyticsContext context,
            AnalyticsBatcherOptions? options = null,
            string? analyticsSessionId = null,
            IAnalyticsScheduler? scheduler = null)
            : this(
                (request, cancellationToken) => api.PostSdkAnalyticsEventsAsync(request, cancellationToken),
                context,
                options,
                analyticsSessionId,
                scheduler ?? new TaskAnalyticsScheduler(),
                () => Guid.NewGuid().ToString("D"),
                () => DateTimeOffset.UtcNow)
        {
            if (api is null) throw new ArgumentNullException(nameof(api));
        }

        internal AnalyticsBatcher(
            Func<IngestAnalyticsRequest, CancellationToken, Task<IngestAnalyticsResponse?>> send,
            AnalyticsContext context,
            AnalyticsBatcherOptions? options,
            string? analyticsSessionId,
            Func<TimeSpan, CancellationToken, Task> delay,
            Func<string> nextEventId,
            Func<DateTimeOffset> utcNow)
            : this(
                send,
                context,
                options,
                analyticsSessionId,
                new TaskAnalyticsScheduler(delay),
                nextEventId,
                utcNow)
        {
        }

        private AnalyticsBatcher(
            Func<IngestAnalyticsRequest, CancellationToken, Task<IngestAnalyticsResponse?>> send,
            AnalyticsContext context,
            AnalyticsBatcherOptions? options,
            string? analyticsSessionId,
            IAnalyticsScheduler scheduler,
            Func<string> nextEventId,
            Func<DateTimeOffset> utcNow)
        {
            this.send = send ?? throw new ArgumentNullException(nameof(send));
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.options = options ?? new AnalyticsBatcherOptions();
            this.options.Validate();
            this.analyticsSessionId = string.IsNullOrWhiteSpace(analyticsSessionId)
                ? Guid.NewGuid().ToString("D")
                : analyticsSessionId!;
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.nextEventId = nextEventId ?? throw new ArgumentNullException(nameof(nextEventId));
            this.utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        }

        /// <summary>
        /// Raised asynchronously, except when the scheduler itself refuses to start. That failure is
        /// delivered inline because the scheduler cannot be trusted to dispatch it.
        /// </summary>
        public event Action<AnalyticsDiagnostic>? Diagnostic;

        public int BufferedEventCount
        {
            get { lock (gate) return bufferedEvents; }
        }

        public long DroppedEventCount => Interlocked.Read(ref droppedEvents);
        public long RetryCount => Interlocked.Read(ref retryCount);

        /// <summary>
        /// Adds an event without waiting for network I/O. Returns false when the configured
        /// memory bound has been reached. Missing event/session ids and occurredAt are filled once.
        /// When the scheduler refuses to start a drain, the queue is dropped (reported as
        /// <see cref="AnalyticsDiagnosticKind.BatchDropped"/>), flushes settle, and the error is rethrown.
        /// </summary>
        public bool TryEnqueue(AnalyticsEvent analyticsEvent)
        {
            if (analyticsEvent is null) throw new ArgumentNullException(nameof(analyticsEvent));

            if (string.IsNullOrWhiteSpace(analyticsEvent.EventId)) analyticsEvent.EventId = nextEventId();
            if (string.IsNullOrWhiteSpace(analyticsEvent.AnalyticsSessionId))
                analyticsEvent.AnalyticsSessionId = analyticsSessionId;
            if (string.IsNullOrWhiteSpace(analyticsEvent.OccurredAt))
                analyticsEvent.OccurredAt = utcNow().ToUniversalTime().ToString("O");

            var startPump = false;
            lock (gate)
            {
                ThrowIfDisposed();
                if (bufferedEvents >= options.MaxBufferedEvents)
                {
                    Interlocked.Increment(ref droppedEvents);
                    Publish(new AnalyticsDiagnostic(
                        AnalyticsDiagnosticKind.QueueFull,
                        new[] { analyticsEvent.EventId! },
                        reason: "analytics queue is full"));
                    return false;
                }

                queue.Enqueue(Snapshot(analyticsEvent));
                bufferedEvents++;
                if (!pumping)
                {
                    pumping = true;
                    idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    startPump = true;
                }
            }

            if (startPump) StartPump();
            return true;
        }

        /// <summary>Convenience overload for gameplay call sites.</summary>
        public bool TryEnqueue(
            string type,
            IReadOnlyDictionary<string, object?>? properties = null,
            DateTimeOffset? occurredAt = null)
        {
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentException("An event type is required.", nameof(type));
            Dictionary<string, IHaivenJsonNode>? encoded = null;
            if (properties is not null)
            {
                encoded = properties.ToDictionary(
                    pair => pair.Key,
                    pair => HaivenJson.Parse(HaivenJson.Serialize(pair.Value)));
            }

            return TryEnqueue(new AnalyticsEvent
            {
                Type = type,
                Properties = encoded,
                OccurredAt = occurredAt?.ToUniversalTime().ToString("O"),
            });
        }

        /// <summary>Completes after every event accepted before the queue becomes idle is sent or dropped.</summary>
        public Task FlushAsync()
        {
            lock (gate) return idle.Task;
        }

        public void Dispose()
        {
            List<string>? queuedIds = null;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                stopped.Cancel();
                if (queue.Count > 0)
                {
                    queuedIds = queue.Select(EventId).ToList();
                    bufferedEvents -= queue.Count;
                    queue.Clear();
                    Interlocked.Add(ref droppedEvents, queuedIds.Count);
                }
                if (!pumping) idle.TrySetResult(true);
            }
            if (queuedIds is not null)
                Publish(new AnalyticsDiagnostic(AnalyticsDiagnosticKind.BatchDropped, queuedIds, reason: "batcher disposed"));
        }

        private void StartPump()
        {
            try
            {
                scheduler.Start(PumpAsync);
            }
            catch (Exception error)
            {
                // A host scheduler can refuse work (a destroyed Unity component
                // throws ObjectDisposedException). No pump owns the queue then, so
                // drop what it holds and settle FlushAsync now; leaving pumping set
                // would make every later flush wait out its caller's timeout.
                List<string> queuedIds;
                lock (gate)
                {
                    pumping = false;
                    queuedIds = queue.Select(EventId).ToList();
                    bufferedEvents -= queue.Count;
                    queue.Clear();
                    idle.TrySetResult(true);
                }
                Interlocked.Add(ref droppedEvents, queuedIds.Count);
                PublishInline(new AnalyticsDiagnostic(
                    AnalyticsDiagnosticKind.BatchDropped,
                    queuedIds,
                    exception: error,
                    reason: "analytics scheduler failed to start"));
                throw;
            }
        }

        private async Task PumpAsync()
        {
            try
            {
                if (options.FlushInterval > TimeSpan.Zero)
                    await scheduler.Delay(options.FlushInterval, stopped.Token);

                while (true)
                {
                    List<AnalyticsEvent> batch;
                    lock (gate)
                    {
                        if (queue.Count == 0)
                        {
                            pumping = false;
                            idle.TrySetResult(true);
                            return;
                        }
                        batch = new List<AnalyticsEvent>(Math.Min(options.BatchSize, queue.Count));
                        while (batch.Count < options.BatchSize && queue.Count > 0)
                            batch.Add(queue.Dequeue());
                    }

                    await SendWithRetryAsync(batch);
                    lock (gate) bufferedEvents -= batch.Count;
                }
            }
            catch (OperationCanceledException)
            {
                var abandoned = 0;
                lock (gate)
                {
                    pumping = false;
                    // A host scheduler can be destroyed independently of this
                    // batcher (Unity tears components down by object lifetime).
                    // Cancellation must settle FlushAsync even when it did not
                    // originate from Dispose's token.
                    abandoned = bufferedEvents;
                    bufferedEvents = 0;
                    queue.Clear();
                    idle.TrySetResult(true);
                }
                if (abandoned > 0) Interlocked.Add(ref droppedEvents, abandoned);
            }
        }

        private async Task SendWithRetryAsync(List<AnalyticsEvent> batch)
        {
            var ids = batch.Select(EventId).ToArray();
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        var response = await send(
                            new IngestAnalyticsRequest { Context = context, Events = batch },
                            stopped.Token);
                        PublishRejections(response, batch);
                        return;
                    }
                    catch (OperationCanceledException) when (stopped.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        if (attempt >= options.MaxRetries)
                        {
                            Interlocked.Add(ref droppedEvents, batch.Count);
                            Publish(new AnalyticsDiagnostic(
                                AnalyticsDiagnosticKind.BatchDropped,
                                ids,
                                attempt + 1,
                                exception: error,
                                reason: "retry limit reached"));
                            return;
                        }

                        var retryAttempt = attempt + 1;
                        var retryDelay = Backoff(retryAttempt);
                        Interlocked.Increment(ref retryCount);
                        Publish(new AnalyticsDiagnostic(
                            AnalyticsDiagnosticKind.RetryScheduled,
                            ids,
                            retryAttempt,
                            retryDelay,
                            error));
                        await scheduler.Delay(retryDelay, stopped.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (stopped.IsCancellationRequested)
            {
                Interlocked.Add(ref droppedEvents, batch.Count);
                Publish(new AnalyticsDiagnostic(
                    AnalyticsDiagnosticKind.BatchDropped, ids, exception: null, reason: "batcher disposed"));
            }
        }

        private void PublishRejections(IngestAnalyticsResponse? response, IReadOnlyList<AnalyticsEvent> batch)
        {
            if (response?.Rejected is null) return;
            foreach (var rejected in response.Rejected)
            {
                var index = rejected.Index ?? -1;
                var ids = index >= 0 && index < batch.Count
                    ? new[] { EventId(batch[(int)index]) }
                    : Array.Empty<string>();
                Publish(new AnalyticsDiagnostic(
                    AnalyticsDiagnosticKind.EventRejected,
                    ids,
                    reason: rejected.Reason ?? "server rejected analytics event"));
            }
        }

        private TimeSpan Backoff(int retryAttempt)
        {
            var multiplier = Math.Pow(2, retryAttempt - 1);
            var ticks = Math.Min(options.InitialRetryDelay.Ticks * multiplier, options.MaxRetryDelay.Ticks);
            return TimeSpan.FromTicks((long)ticks);
        }

        private void Publish(AnalyticsDiagnostic diagnostic)
        {
            var handlers = Diagnostic;
            if (handlers is null) return;
            scheduler.Post(() => NotifyHandlers(handlers, diagnostic));
        }

        private void PublishInline(AnalyticsDiagnostic diagnostic)
        {
            var handlers = Diagnostic;
            if (handlers is null) return;
            NotifyHandlers(handlers, diagnostic);
        }

        private static void NotifyHandlers(Action<AnalyticsDiagnostic> handlers, AnalyticsDiagnostic diagnostic)
        {
            foreach (Action<AnalyticsDiagnostic> handler in handlers.GetInvocationList())
            {
                try { handler(diagnostic); }
                catch { /* Diagnostics must not stop or delay delivery. */ }
            }
        }

        private static AnalyticsEvent Snapshot(AnalyticsEvent analyticsEvent)
        {
            Dictionary<string, IHaivenJsonNode>? properties = null;
            if (analyticsEvent.Properties is not null)
            {
                properties = analyticsEvent.Properties.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);
            }

            return new AnalyticsEvent
            {
                AnalyticsSessionId = analyticsEvent.AnalyticsSessionId,
                EventId = analyticsEvent.EventId,
                OccurredAt = analyticsEvent.OccurredAt,
                Properties = properties,
                Type = analyticsEvent.Type,
            };
        }

        private static string EventId(AnalyticsEvent analyticsEvent) => analyticsEvent.EventId ?? string.Empty;

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            signal.SetResult(true);
            return signal;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(AnalyticsBatcher));
        }

        private sealed class TaskAnalyticsScheduler : IAnalyticsScheduler
        {
            private readonly Func<TimeSpan, CancellationToken, Task> delay;

            public TaskAnalyticsScheduler()
                : this((duration, cancellationToken) => Task.Delay(duration, cancellationToken))
            {
            }

            public TaskAnalyticsScheduler(Func<TimeSpan, CancellationToken, Task> delay)
            {
                this.delay = delay ?? throw new ArgumentNullException(nameof(delay));
            }

            public void Start(Func<Task> operation) => _ = Task.Run(operation);

            public Task Delay(TimeSpan duration, CancellationToken cancellationToken) =>
                delay(duration, cancellationToken);

            public void Post(Action callback) => _ = Task.Run(callback);
        }
    }
}
