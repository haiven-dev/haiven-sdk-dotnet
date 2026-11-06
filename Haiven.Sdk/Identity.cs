#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Contracts;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk
{
    /// <summary>
    /// Owns the browser context used for account linking. An implementation must
    /// open its popup synchronously, issue the link POST from that popup's cookie
    /// context, and navigate that same popup to the returned redirect.
    /// </summary>
    /// <remarks>
    /// The ordinary game REST transport cannot satisfy this contract: the
    /// federation-state cookie must be created in the browser that follows it.
    /// </remarks>
    public interface IIdentityLinkBrowserContext
    {
        Task<IdentityLinkLaunch> BeginLinkAsync(
            string provider,
            CancellationToken cancellationToken);
    }

    /// <summary>The finite result of handing an account-link flow to a browser context.</summary>
    public sealed class IdentityLinkLaunch
    {
        private IdentityLinkLaunch(bool succeeded, string? errorCode, string? detail)
        {
            Succeeded = succeeded;
            ErrorCode = errorCode;
            Detail = detail;
        }

        public bool Succeeded { get; }
        public string? ErrorCode { get; }
        public string? Detail { get; }

        public static IdentityLinkLaunch Opened() => new IdentityLinkLaunch(true, null, null);

        public static IdentityLinkLaunch Failed(string errorCode, string? detail = null)
        {
            if (string.IsNullOrWhiteSpace(errorCode))
                throw new ArgumentException("An account-link failure needs an error code.", nameof(errorCode));
            var normalized = errorCode.Trim();
            if (!HaivenIdentity.IsFiniteCode(normalized))
                throw new ArgumentException("An account-link failure code must be lowercase snake case.", nameof(errorCode));
            return new IdentityLinkLaunch(false, normalized, detail);
        }
    }

    /// <summary>An immutable account identity suitable for a game UI.</summary>
    public sealed class HaivenIdentityMethod
    {
        internal HaivenIdentityMethod(string provider, string email)
        {
            Provider = provider;
            Email = email;
        }

        public string Provider { get; }
        public string Email { get; }
    }

    /// <summary>An immutable sign-in-method snapshot suitable for a game UI.</summary>
    public sealed class HaivenSignInMethods
    {
        internal HaivenSignInMethods(bool hasPassword, IList<HaivenIdentityMethod> identities)
        {
            HasPassword = hasPassword;
            Identities = new ReadOnlyCollection<HaivenIdentityMethod>(identities);
        }

        public bool HasPassword { get; }
        public IReadOnlyList<HaivenIdentityMethod> Identities { get; }
    }

    /// <summary>An immutable, validated Message of the Day suitable for a game UI.</summary>
    public sealed class HaivenMotd
    {
        internal HaivenMotd(
            string id,
            string? title,
            string body,
            Uri? url,
            DateTimeOffset startsAt,
            DateTimeOffset? endsAt)
        {
            Id = id;
            Title = title;
            Body = body;
            Url = url;
            StartsAt = startsAt;
            EndsAt = endsAt;
        }

        public string Id { get; }
        public string? Title { get; }
        public string Body { get; }
        public Uri? Url { get; }
        public DateTimeOffset StartsAt { get; }
        public DateTimeOffset? EndsAt { get; }
    }

    public sealed class IdentityLinkFailure
    {
        internal IdentityLinkFailure(string provider, string code)
        {
            Provider = provider;
            Code = code;
        }

        public string Provider { get; }
        public string Code { get; }
    }

    /// <summary>A semantic or lifecycle failure in the high-level identity facade.</summary>
    public sealed class HaivenIdentityException : Exception
    {
        public HaivenIdentityException(string code)
            : base("Haiven identity operation failed: " + code)
        {
            Code = code;
        }

        public string Code { get; }
    }

    /// <summary>
    /// Joins MOTD REST/realtime state, sign-in methods, link outcomes, unlinking,
    /// and the host-owned browser account-link boundary for engine UIs.
    /// </summary>
    public sealed class HaivenIdentity : IDisposable
    {
        private readonly object sync = new object();
        private readonly RestApi rest;
        private readonly string integrationId;
        private readonly IIdentityLinkBrowserContext? browserContext;
        private readonly List<Action> routerSubscriptions = new List<Action>();
        private readonly HashSet<string> dismissedMotdIds = new HashSet<string>(StringComparer.Ordinal);
        private HaivenMotd? currentMotd;
        private HaivenSignInMethods? currentMethods;
        private long motdRevision;
        private long methodsRevision;
        private long accountGeneration;
        private long routerGeneration;
        private long nextRouterGeneration;
        private bool disposed;

        public HaivenIdentity(
            RestApi rest,
            string integrationId,
            IIdentityLinkBrowserContext? browserContext = null)
        {
            this.rest = rest ?? throw new ArgumentNullException(nameof(rest));
            this.integrationId = string.IsNullOrWhiteSpace(integrationId)
                ? throw new ArgumentException("An integration id is required.", nameof(integrationId))
                : integrationId.Trim();
            this.browserContext = browserContext;
        }

        public event Action<HaivenMotd?>? MotdChanged;
        public event Action<HaivenSignInMethods?>? SignInMethodsChanged;
        public event Action<HaivenIdentityMethod>? IdentityLinked;
        public event Action<IdentityLinkFailure>? IdentityLinkFailed;
        public event Action<string>? Diagnostic;

        public bool CanBeginLink => browserContext is not null;

        public HaivenMotd? CurrentMotd
        {
            get { lock (sync) return currentMotd; }
        }

        public HaivenSignInMethods? CurrentSignInMethods
        {
            get { lock (sync) return currentMethods; }
        }

        public async Task<HaivenMotd?> LoadMotdAsync(CancellationToken cancellationToken = default)
        {
            long revision;
            lock (sync)
            {
                ThrowIfDisposed();
                revision = motdRevision;
            }

            var response = await rest.GetMotdAsync(integrationId, cancellationToken).ConfigureAwait(false);
            var value = response is null ? null : NormalizeMotd(response);
            lock (sync)
            {
                ThrowIfDisposed();
                if (revision != motdRevision) throw new HaivenIdentityException("superseded");
                value = ApplyMotdLocked(value);
            }
            return value;
        }

        public async Task<HaivenSignInMethods> LoadSignInMethodsAsync(
            CancellationToken cancellationToken = default)
        {
            long generation;
            long revision;
            lock (sync)
            {
                ThrowIfDisposed();
                generation = accountGeneration;
                revision = methodsRevision;
            }

            var response = await rest.GetUsersMeSignInMethodsAsync(cancellationToken).ConfigureAwait(false);
            var value = NormalizeMethods(response);
            lock (sync)
            {
                ThrowIfDisposed();
                if (generation != accountGeneration) throw new HaivenIdentityException("session_changed");
                if (revision != methodsRevision) throw new HaivenIdentityException("superseded");
                methodsRevision++;
                currentMethods = value;
                SignInMethodsChanged?.Invoke(value);
            }
            return value;
        }

        /// <summary>
        /// Begins linking only through the injected browser context. Success means
        /// the browser flow opened; the realtime linked/failed event is the outcome.
        /// </summary>
        public async Task<IdentityLinkLaunch> BeginLinkAsync(
            string provider,
            CancellationToken cancellationToken = default)
        {
            var normalized = NormalizeProvider(provider);
            long generation;
            IIdentityLinkBrowserContext context;
            lock (sync)
            {
                ThrowIfDisposed();
                generation = accountGeneration;
                context = browserContext ?? throw new HaivenIdentityException("browser_context_required");
            }

            // Calling directly (before this method yields) lets browser hosts open
            // a popup synchronously in response to the player's button gesture.
            var pending = context.BeginLinkAsync(normalized, cancellationToken);
            if (pending is null) throw new HaivenIdentityException("invalid_browser_context_result");
            var launch = await pending.ConfigureAwait(false)
                ?? throw new HaivenIdentityException("invalid_browser_context_result");
            lock (sync)
            {
                ThrowIfDisposed();
                if (generation != accountGeneration) throw new HaivenIdentityException("session_changed");
            }
            if (launch.Succeeded && (launch.ErrorCode is not null || launch.Detail is not null))
                throw new HaivenIdentityException("invalid_browser_context_result");
            if (!launch.Succeeded && string.IsNullOrWhiteSpace(launch.ErrorCode))
                throw new HaivenIdentityException("invalid_browser_context_result");
            return launch;
        }

        public async Task UnlinkAsync(string provider, CancellationToken cancellationToken = default)
        {
            var normalized = NormalizeProvider(provider);
            long generation;
            long revision;
            lock (sync)
            {
                ThrowIfDisposed();
                generation = accountGeneration;
                revision = methodsRevision;
            }

            await rest.DeleteUsersMeSignInMethodsSsoProviderAsync(normalized, cancellationToken)
                .ConfigureAwait(false);
            HaivenSignInMethods? changed = null;
            lock (sync)
            {
                ThrowIfDisposed();
                if (generation != accountGeneration) throw new HaivenIdentityException("session_changed");
                if (revision != methodsRevision) throw new HaivenIdentityException("superseded");
                methodsRevision++;
                if (currentMethods is not null)
                {
                    var identities = new List<HaivenIdentityMethod>();
                    foreach (var identity in currentMethods.Identities)
                        if (!string.Equals(identity.Provider, normalized, StringComparison.Ordinal))
                            identities.Add(identity);
                    currentMethods = new HaivenSignInMethods(currentMethods.HasPassword, identities);
                    changed = currentMethods;
                }
                if (changed is not null) SignInMethodsChanged?.Invoke(changed);
            }
        }

        /// <summary>Routes link/MOTD events from the currently authenticated session.</summary>
        public void Attach(WsEventRouter router) => Attach(router, null);

        internal void Attach(WsEventRouter router, Action? afterReservation)
        {
            if (router is null) throw new ArgumentNullException(nameof(router));
            long generation;
            long account;
            lock (sync)
            {
                ThrowIfDisposed();
                // Reserve before registration so overlapping Attach calls can
                // never give two routers the same accepted callback token.
                generation = ++nextRouterGeneration;
                account = accountGeneration;
            }
            afterReservation?.Invoke();

            var subscriptions = new List<Action>();
            try
            {
                subscriptions.Add(router.OnAccountIdentityLinked(value => OnLinked(value, generation)));
                subscriptions.Add(router.OnAccountIdentityLinkFailed(value => OnLinkFailed(value, generation)));
                subscriptions.Add(router.OnMotd(value => OnMotd(value, generation)));
            }
            catch
            {
                foreach (var unsubscribe in subscriptions) unsubscribe();
                throw;
            }

            List<Action> previous;
            try
            {
                lock (sync)
                {
                    ThrowIfDisposed();
                    if (account != accountGeneration)
                        throw new HaivenIdentityException("session_changed");
                    if (generation != nextRouterGeneration)
                        throw new HaivenIdentityException("superseded");
                    routerGeneration = generation;
                    previous = new List<Action>(routerSubscriptions);
                    routerSubscriptions.Clear();
                    routerSubscriptions.AddRange(subscriptions);
                }
            }
            catch
            {
                foreach (var unsubscribe in subscriptions) unsubscribe();
                throw;
            }
            foreach (var unsubscribe in previous) unsubscribe();
        }

        /// <summary>Clears account-owned state and rejects late operations/events.</summary>
        public void ClearAccount()
        {
            List<Action> subscriptions;
            lock (sync)
            {
                ThrowIfDisposed();
                accountGeneration++;
                methodsRevision++;
                routerGeneration = ++nextRouterGeneration;
                currentMethods = null;
                subscriptions = new List<Action>(routerSubscriptions);
                routerSubscriptions.Clear();
                SignInMethodsChanged?.Invoke(null);
            }
            foreach (var unsubscribe in subscriptions) unsubscribe();
        }

        public void DismissMotd()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (currentMotd is null) return;
                DismissMotdLocked(currentMotd.Id);
            }
        }

        /// <summary>
        /// Dismisses only when the named MOTD is still current. Presentation
        /// layers use this compare-and-dismiss form so a delayed click cannot
        /// dismiss a newer realtime replacement.
        /// </summary>
        public bool DismissMotd(string expectedId)
        {
            if (string.IsNullOrWhiteSpace(expectedId))
                throw new ArgumentException("A MOTD id is required.", nameof(expectedId));
            lock (sync)
            {
                ThrowIfDisposed();
                if (currentMotd is null
                    || !string.Equals(currentMotd.Id, expectedId, StringComparison.Ordinal))
                    return false;
                DismissMotdLocked(expectedId);
                return true;
            }
        }

        public void Dispose()
        {
            List<Action> subscriptions;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                accountGeneration++;
                motdRevision++;
                methodsRevision++;
                routerGeneration = ++nextRouterGeneration;
                subscriptions = new List<Action>(routerSubscriptions);
                routerSubscriptions.Clear();
            }
            foreach (var unsubscribe in subscriptions) unsubscribe();
        }

        private void OnLinked(AccountIdentityLinkedV0 value, long generation)
        {
            lock (sync) if (disposed || generation != routerGeneration) return;
            HaivenIdentityMethod identity;
            try { identity = NormalizeIdentity(value.Provider, value.Email); }
            catch (HaivenIdentityException error)
            {
                lock (sync)
                {
                    if (disposed || generation != routerGeneration) return;
                    Diagnostic?.Invoke(error.Code);
                }
                return;
            }

            HaivenSignInMethods? changed = null;
            lock (sync)
            {
                if (disposed || generation != routerGeneration) return;
                methodsRevision++;
                if (currentMethods is not null)
                {
                    var identities = new List<HaivenIdentityMethod>();
                    foreach (var existing in currentMethods.Identities)
                        if (!string.Equals(existing.Provider, identity.Provider, StringComparison.Ordinal))
                            identities.Add(existing);
                    identities.Add(identity);
                    currentMethods = new HaivenSignInMethods(currentMethods.HasPassword, identities);
                    changed = currentMethods;
                }
                if (changed is not null) SignInMethodsChanged?.Invoke(changed);
                // A synchronous state callback may replace the account/router.
                if (disposed || generation != routerGeneration) return;
                IdentityLinked?.Invoke(identity);
            }
        }

        private void OnLinkFailed(AccountIdentityLinkFailedV0 value, long generation)
        {
            lock (sync) if (disposed || generation != routerGeneration) return;
            IdentityLinkFailure failure;
            try
            {
                var provider = NormalizeProvider(value.Provider);
                if (!IsFiniteCode(value.Code)) throw new HaivenIdentityException("invalid_link_failure_event");
                failure = new IdentityLinkFailure(provider, value.Code);
            }
            catch (HaivenIdentityException error)
            {
                lock (sync)
                {
                    if (disposed || generation != routerGeneration) return;
                    Diagnostic?.Invoke(error.Code);
                }
                return;
            }
            lock (sync)
            {
                if (disposed || generation != routerGeneration) return;
                IdentityLinkFailed?.Invoke(failure);
            }
        }

        private void OnMotd(MotdV0 value, long generation)
        {
            lock (sync) if (disposed || generation != routerGeneration) return;
            HaivenMotd motd;
            try { motd = NormalizeMotd(value); }
            catch (HaivenIdentityException error)
            {
                lock (sync)
                {
                    if (disposed || generation != routerGeneration) return;
                    Diagnostic?.Invoke(error.Code);
                }
                return;
            }
            lock (sync)
            {
                if (disposed || generation != routerGeneration) return;
                ApplyMotdLocked(motd);
            }
        }

        private HaivenMotd? ApplyMotdLocked(HaivenMotd? value)
        {
            if (value is not null && dismissedMotdIds.Contains(value.Id)) value = null;
            if (currentMotd is null && value is null) return null;
            if (currentMotd is not null && value is not null
                && string.Equals(currentMotd.Id, value.Id, StringComparison.Ordinal))
                return currentMotd;
            motdRevision++;
            currentMotd = value;
            MotdChanged?.Invoke(value);
            return value;
        }

        private void DismissMotdLocked(string id)
        {
            dismissedMotdIds.Add(id);
            motdRevision++;
            currentMotd = null;
            MotdChanged?.Invoke(null);
        }

        private static HaivenMotd NormalizeMotd(Motd value) => NormalizeMotd(
            value.Id, value.Title, value.Body, value.Url, value.StartsAt, value.EndsAt);

        private static HaivenMotd NormalizeMotd(MotdV0 value) => NormalizeMotd(
            value.Id, value.Title, value.Body, value.Url, value.StartsAt, value.EndsAt);

        private static HaivenMotd NormalizeMotd(
            string? id,
            string? title,
            string? body,
            string? url,
            string? startsAt,
            string? endsAt)
        {
            if (!Guid.TryParse(id, out var parsedId) || string.IsNullOrWhiteSpace(body)
                || !TryTimestamp(startsAt, out var parsedStart))
                throw new HaivenIdentityException("invalid_motd");
            Uri? parsedUrl = null;
            if (url is not null
                && Uri.TryCreate(url, UriKind.Absolute, out var candidateUrl)
                && candidateUrl.Scheme == Uri.UriSchemeHttps
                && !string.IsNullOrEmpty(candidateUrl.Host)
                && string.IsNullOrEmpty(candidateUrl.UserInfo))
                parsedUrl = candidateUrl;
            DateTimeOffset? parsedEnd = null;
            if (endsAt is not null)
            {
                if (!TryTimestamp(endsAt, out var end))
                    throw new HaivenIdentityException("invalid_motd");
                parsedEnd = end;
            }
            if (parsedEnd.HasValue && parsedEnd.Value < parsedStart)
                throw new HaivenIdentityException("invalid_motd");
            return new HaivenMotd(
                parsedId.ToString("D"), title, body, parsedUrl, parsedStart, parsedEnd);
        }

        private static HaivenSignInMethods NormalizeMethods(SignInMethodsResponse? value)
        {
            if (value?.HasPassword is null || value.Identities is null)
                throw new HaivenIdentityException("invalid_sign_in_methods");
            var identities = new List<HaivenIdentityMethod>();
            var providers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var identity in value.Identities)
            {
                var normalized = NormalizeIdentity(identity.Provider, identity.Email);
                if (!providers.Add(normalized.Provider))
                    throw new HaivenIdentityException("invalid_sign_in_methods");
                identities.Add(normalized);
            }
            return new HaivenSignInMethods(value.HasPassword.Value, identities);
        }

        private static HaivenIdentityMethod NormalizeIdentity(string? provider, string? email)
        {
            var normalized = NormalizeProvider(provider);
            if (string.IsNullOrWhiteSpace(email))
                throw new HaivenIdentityException("invalid_identity");
            return new HaivenIdentityMethod(normalized, email.Trim());
        }

        private static string NormalizeProvider(string? provider)
        {
            var normalized = provider?.Trim().ToLowerInvariant();
            if (normalized != "google" && normalized != "apple")
                throw new HaivenIdentityException("invalid_provider");
            return normalized;
        }

        internal static bool IsFiniteCode(string? value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (var character in value)
                if (!(character == '_' || (character >= 'a' && character <= 'z')
                    || (character >= '0' && character <= '9')))
                    return false;
            return true;
        }

        private static bool TryTimestamp(string? value, out DateTimeOffset parsed) =>
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(HaivenIdentity));
        }
    }
}
