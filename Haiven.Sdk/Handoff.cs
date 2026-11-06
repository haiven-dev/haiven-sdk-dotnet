using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Haiven.Sdk.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Haiven.Sdk
{
    public sealed class HandoffOptions
    {
        private TimeSpan transactionLifetime = TimeSpan.FromMinutes(15);

        public HandoffOptions(Uri entryUri, Uri exchangeUri, string integrationId, Uri redirectUri)
        {
            EntryUri = entryUri ?? throw new ArgumentNullException(nameof(entryUri));
            ExchangeUri = exchangeUri ?? throw new ArgumentNullException(nameof(exchangeUri));
            IntegrationId = !string.IsNullOrWhiteSpace(integrationId)
                ? integrationId
                : throw new ArgumentException("An integration id is required.", nameof(integrationId));
            RedirectUri = redirectUri ?? throw new ArgumentNullException(nameof(redirectUri));

            RequireSecureWebUri(EntryUri, nameof(entryUri));
            RequireSecureWebUri(ExchangeUri, nameof(exchangeUri));
            if (!RedirectUri.IsAbsoluteUri)
                throw new ArgumentException("The redirect URI must be absolute.", nameof(redirectUri));
            if (!string.IsNullOrEmpty(RedirectUri.Fragment))
                throw new ArgumentException("The redirect URI must not contain a fragment.", nameof(redirectUri));
        }

        public Uri EntryUri { get; }
        public Uri ExchangeUri { get; }
        public string IntegrationId { get; }
        public Uri RedirectUri { get; }
        public string? InstructionsId { get; set; }
        public string? ScreenHint { get; set; }
        public TimeSpan TransactionLifetime
        {
            get => transactionLifetime;
            set => transactionLifetime = value > TimeSpan.Zero
                ? value
                : throw new ArgumentOutOfRangeException(nameof(value));
        }

        private static void RequireSecureWebUri(Uri uri, string parameter)
        {
            if (!uri.IsAbsoluteUri)
                throw new ArgumentException("The URI must be absolute.", parameter);
            if (uri.Scheme == Uri.UriSchemeHttps) return;
            if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) return;
            throw new ArgumentException("The URI must use HTTPS (HTTP is allowed only for loopback development).", parameter);
        }
    }

    public sealed class HandoffStart
    {
        internal HandoffStart(Uri authorizationUri, string state)
        {
            AuthorizationUri = authorizationUri;
            State = state;
        }

        public Uri AuthorizationUri { get; }
        public string State { get; }
    }

    public sealed class HandoffTransaction
    {
        public HandoffTransaction(string state, string codeVerifier, DateTimeOffset createdAt)
        {
            State = state;
            CodeVerifier = codeVerifier;
            CreatedAt = createdAt;
        }

        public string State { get; }
        public string CodeVerifier { get; }
        public DateTimeOffset CreatedAt { get; }
    }

    public sealed class HandoffExchangeRequest
    {
        public HandoffExchangeRequest(string code, string codeVerifier, string integrationId, Uri redirectUri)
        {
            Code = code;
            CodeVerifier = codeVerifier;
            IntegrationId = integrationId;
            RedirectUri = redirectUri;
        }

        public string Code { get; }
        public string CodeVerifier { get; }
        public string IntegrationId { get; }
        public Uri RedirectUri { get; }

        public string ToFormBody()
        {
            return "code=" + Escape(Code)
                + "&code_verifier=" + Escape(CodeVerifier)
                + "&integration_id=" + Escape(IntegrationId)
                + "&redirect_uri=" + Escape(RedirectUri.AbsoluteUri);
        }

        private static string Escape(string value) => Uri.EscapeDataString(value);
    }

    public sealed class HandoffExchangeResponse
    {
        public HandoffExchangeResponse(string accessToken, string tokenType, long? expiresInSeconds)
        {
            AccessToken = !string.IsNullOrEmpty(accessToken)
                ? accessToken
                : throw new ArgumentException("The exchange returned no access token.", nameof(accessToken));
            TokenType = !string.IsNullOrEmpty(tokenType)
                ? tokenType
                : throw new ArgumentException("The exchange returned no token type.", nameof(tokenType));
            ExpiresInSeconds = expiresInSeconds is null || expiresInSeconds >= 0
                ? expiresInSeconds
                : throw new ArgumentOutOfRangeException(nameof(expiresInSeconds));
        }

        public string AccessToken { get; }
        public string TokenType { get; }

        /// <summary>
        /// Seconds left on the bearer, or null for a durable credential that lasts
        /// until it is revoked (ADR 0041).
        /// </summary>
        public long? ExpiresInSeconds { get; }

        public static HandoffExchangeResponse ParseJson(string json)
        {
            try
            {
                var root = HaivenJson.Parse(json);
                HaivenJsonRead.ExpectObject(root, "exchange");
                if (root.TryGetProperty("refresh_token", out _))
                    throw new HandoffException("The handoff exchange returned a forbidden refresh credential.");
                return new HandoffExchangeResponse(
                    Member(root, "access_token", HaivenJsonRead.NullableString) ?? "",
                    Member(root, "token_type", HaivenJsonRead.NullableString) ?? "",
                    // Present but null for a durable credential (ADR 0041).
                    Member(root, "expires_in", HaivenJsonRead.NullableInt64));
            }
            catch (HandoffException)
            {
                throw;
            }
            catch (Exception error) when (
                error is HaivenJsonException
                || error is InvalidOperationException
                || error is KeyNotFoundException
                || error is ArgumentException
                || error is OverflowException)
            {
                throw new HandoffException("The handoff exchange returned an invalid credential.", error);
            }
        }

        private static T Member<T>(IHaivenJsonNode root, string name, Func<IHaivenJsonNode, string, T> read) =>
            root.TryGetProperty(name, out var value) ? read(value, "exchange." + name) : throw HaivenJsonRead.Missing("exchange." + name);
    }

    public sealed class HandoffCredential
    {
        public HandoffCredential(string accessToken, string tokenType, DateTimeOffset? expiresAt)
        {
            AccessToken = accessToken;
            TokenType = tokenType;
            ExpiresAt = expiresAt;
        }

        public string AccessToken { get; }
        public string TokenType { get; }

        /// <summary>
        /// When the bearer stops working, or null for a durable credential that
        /// lasts until it is revoked (ADR 0041).
        /// </summary>
        public DateTimeOffset? ExpiresAt { get; }

        /// <summary>True at or after a finite expiry. A durable credential never expires.</summary>
        public bool HasExpired(DateTimeOffset now) => ExpiresAt.HasValue && now >= ExpiresAt.Value;
    }

    /// <summary>The finite events that invalidate an opaque game credential.</summary>
    public enum HandoffCredentialEndReason
    {
        Expired,
        RestUnauthorized,
        WebSocketExpired,
    }

    public interface IHandoffBrowser
    {
        void Open(Uri uri);
    }

    public interface IHandoffTransactionStore
    {
        void Put(HandoffTransaction transaction);
        HandoffTransaction? Take(string state);
        void Remove(string state);
    }

    public interface IHandoffTokenStore
    {
        void Store(HandoffCredential credential);
        void Clear();
    }

    /// <summary>
    /// A token store that keeps the credential across restarts, so
    /// <see cref="HandoffCoordinator.Resume"/> can reuse it instead of opening the
    /// browser. A shipped game supplies one backed by persistent storage; the
    /// included <see cref="MemoryHandoffTokenStore"/> forgets it on exit and is
    /// for development only.
    /// </summary>
    public interface IHandoffCredentialStore : IHandoffTokenStore
    {
        /// <summary>The credential saved earlier, or null when none is saved.</summary>
        HandoffCredential? Load();
    }

    public interface IHandoffHttpTransport
    {
        Task<HandoffExchangeResponse> ExchangeAsync(
            Uri exchangeUri,
            HandoffExchangeRequest request,
            CancellationToken cancellationToken);
    }

    public interface IHandoffDispatcher
    {
        Task RunAsync(Action action, CancellationToken cancellationToken);
    }

    public interface IHandoffClock
    {
        DateTimeOffset UtcNow { get; }
    }

    public sealed class MemoryHandoffTransactionStore : IHandoffTransactionStore
    {
        private readonly object gate = new object();
        private readonly Dictionary<string, HandoffTransaction> transactions =
            new Dictionary<string, HandoffTransaction>(StringComparer.Ordinal);

        public void Put(HandoffTransaction transaction)
        {
            if (transaction is null) throw new ArgumentNullException(nameof(transaction));
            lock (gate) transactions[transaction.State] = transaction;
        }

        public HandoffTransaction? Take(string state)
        {
            lock (gate)
            {
                if (!transactions.TryGetValue(state, out var transaction)) return null;
                transactions.Remove(state);
                return transaction;
            }
        }

        public void Remove(string state)
        {
            lock (gate) transactions.Remove(state);
        }
    }

    /// <summary>
    /// Keeps the credential in memory only, so every launch needs a new handoff.
    /// For development and tests; a shipped game supplies a persistent
    /// <see cref="IHandoffCredentialStore"/>.
    /// </summary>
    public sealed class MemoryHandoffTokenStore : IHandoffCredentialStore
    {
        private readonly object gate = new object();
        private HandoffCredential? credential;

        public HandoffCredential? Credential
        {
            get { lock (gate) return credential; }
        }

        public HandoffCredential? Load()
        {
            lock (gate) return credential;
        }

        public void Store(HandoffCredential value)
        {
            if (value is null) throw new ArgumentNullException(nameof(value));
            lock (gate) credential = value;
        }

        public void Clear()
        {
            lock (gate) credential = null;
        }
    }

    public sealed class ImmediateHandoffDispatcher : IHandoffDispatcher
    {
        public Task RunAsync(Action action, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }

    public sealed class SystemHandoffClock : IHandoffClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    public sealed class HandoffException : Exception
    {
        public HandoffException(string message) : base(message) { }
        public HandoffException(string message, Exception innerException) : base(message, innerException) { }
    }

    public sealed class HandoffCoordinator
    {
        private readonly HandoffOptions options;
        private readonly IHandoffBrowser browser;
        private readonly IHandoffTransactionStore transactions;
        private readonly IHandoffTokenStore tokens;
        private readonly IHandoffHttpTransport http;
        private readonly IHandoffDispatcher dispatcher;
        private readonly IHandoffClock clock;

        public HandoffCoordinator(
            HandoffOptions options,
            IHandoffBrowser browser,
            IHandoffTransactionStore transactions,
            IHandoffTokenStore tokens,
            IHandoffHttpTransport http,
            IHandoffDispatcher dispatcher,
            IHandoffClock? clock = null)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.browser = browser ?? throw new ArgumentNullException(nameof(browser));
            this.transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
            this.tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
            this.http = http ?? throw new ArgumentNullException(nameof(http));
            this.dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            this.clock = clock ?? new SystemHandoffClock();
        }

        public event Action<HandoffCredential>? Completed;
        public event Action<Exception>? Failed;

        public HandoffStart BeginLogin()
        {
            var verifier = RandomBase64Url(32);
            var state = RandomBase64Url(32);
            var challenge = Sha256Base64Url(verifier);
            var transaction = new HandoffTransaction(state, verifier, clock.UtcNow);
            transactions.Put(transaction);

            var uri = BuildAuthorizationUri(state, challenge);
            try
            {
                browser.Open(uri);
            }
            catch
            {
                transactions.Remove(state);
                throw;
            }

            return new HandoffStart(uri, state);
        }

        /// <summary>
        /// Reuses the credential an <see cref="IHandoffCredentialStore"/> saved, so a
        /// restart does not send the player back to the browser. Call it before
        /// <see cref="BeginLogin"/>; it returns null when nothing valid is saved, and
        /// the caller then begins a handoff. A saved credential past its expiry is
        /// cleared. A reused credential raises <see cref="Completed"/> on the calling
        /// thread, like a finished handoff.
        /// </summary>
        public HandoffCredential? Resume()
        {
            if (!(tokens is IHandoffCredentialStore store)) return null;
            var saved = store.Load();
            if (saved is null) return null;
            if (string.IsNullOrEmpty(saved.AccessToken) || saved.HasExpired(clock.UtcNow))
            {
                tokens.Clear();
                return null;
            }
            Completed?.Invoke(saved);
            return saved;
        }

        /// <summary>
        /// Clears a handoff bearer rejected by local expiry, REST 401, or
        /// auth.expired, then starts a fresh handoff. There is deliberately no
        /// web refresh-token path for a game credential.
        /// </summary>
        public HandoffStart RestartLogin(HandoffCredentialEndReason reason)
        {
            if (!Enum.IsDefined(typeof(HandoffCredentialEndReason), reason))
                throw new ArgumentOutOfRangeException(nameof(reason));
            tokens.Clear();
            return BeginLogin();
        }

        public async Task<HandoffCredential> CompleteAsync(Uri callbackUri, CancellationToken cancellationToken = default)
        {
            if (callbackUri is null) throw new ArgumentNullException(nameof(callbackUri));
            if (!callbackUri.IsAbsoluteUri) throw new HandoffException("The handoff callback URI must be absolute.");
            if (!IsExpectedCallback(callbackUri))
                throw new HandoffException("The handoff callback does not match the configured redirect URI.");

            var query = ParseQuery(callbackUri.Query);
            if (!query.TryGetValue("state", out var state) || string.IsNullOrEmpty(state))
                throw new HandoffException("The handoff callback has no state.");
            if (query.TryGetValue("error", out var callbackError) && !string.IsNullOrEmpty(callbackError))
            {
                transactions.Remove(state);
                throw new HandoffException("The handoff callback failed: " + callbackError);
            }
            if (!query.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            {
                transactions.Remove(state);
                throw new HandoffException("The handoff callback has no code.");
            }

            var transaction = transactions.Take(state);
            if (transaction is null)
                throw new HandoffException("The handoff state is unknown, expired, or already consumed.");
            if (clock.UtcNow - transaction.CreatedAt > options.TransactionLifetime)
                throw new HandoffException("The handoff transaction has expired.");

            cancellationToken.ThrowIfCancellationRequested();
            var response = await http.ExchangeAsync(
                options.ExchangeUri,
                new HandoffExchangeRequest(code, transaction.CodeVerifier, options.IntegrationId, options.RedirectUri),
                cancellationToken);
            var credential = new HandoffCredential(
                response.AccessToken,
                response.TokenType,
                response.ExpiresInSeconds is long seconds ? clock.UtcNow.AddSeconds(seconds) : (DateTimeOffset?)null);

            await dispatcher.RunAsync(() =>
            {
                tokens.Store(credential);
                Completed?.Invoke(credential);
            }, cancellationToken);

            return credential;
        }

        public async Task HandleCallbackAsync(Uri callbackUri, CancellationToken cancellationToken = default)
        {
            try
            {
                await CompleteAsync(callbackUri, cancellationToken);
            }
            catch (Exception error)
            {
                await dispatcher.RunAsync(() => Failed?.Invoke(error), CancellationToken.None);
            }
        }

        public static string Sha256Base64Url(string verifier)
        {
            if (verifier is null) throw new ArgumentNullException(nameof(verifier));
            using (var sha256 = SHA256.Create())
                return Base64Url(sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
        }

        private Uri BuildAuthorizationUri(string state, string challenge)
        {
            var parameters = ParseQuery(options.EntryUri.Query);
            parameters["integration_id"] = options.IntegrationId;
            parameters["completion"] = "redirect";
            parameters["redirect_uri"] = options.RedirectUri.AbsoluteUri;
            parameters["state"] = state;
            parameters["code_challenge"] = challenge;
            parameters["code_challenge_method"] = "S256";
            AddOptional(parameters, "instructions_id", options.InstructionsId);
            AddOptional(parameters, "screen_hint", options.ScreenHint);

            var query = new StringBuilder();
            foreach (var pair in parameters)
            {
                if (query.Length > 0) query.Append('&');
                query.Append(Uri.EscapeDataString(pair.Key));
                query.Append('=');
                query.Append(Uri.EscapeDataString(pair.Value));
            }

            var builder = new UriBuilder(options.EntryUri) { Query = query.ToString() };
            return builder.Uri;
        }

        private static void AddOptional(IDictionary<string, string> values, string key, string? value)
        {
            if (!string.IsNullOrEmpty(value)) values[key] = value;
        }

        private bool IsExpectedCallback(Uri callbackUri)
        {
            var expected = options.RedirectUri;
            if (!string.Equals(callbackUri.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(callbackUri.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
                || callbackUri.Port != expected.Port
                || !string.Equals(callbackUri.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal))
                return false;

            var callbackQuery = ParseQuery(callbackUri.Query);
            foreach (var pair in ParseQuery(expected.Query))
                if (!callbackQuery.TryGetValue(pair.Key, out var value)
                    || !string.Equals(value, pair.Value, StringComparison.Ordinal))
                    return false;
            return true;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var text = query.StartsWith("?", StringComparison.Ordinal) ? query.Substring(1) : query;
            if (text.Length == 0) return values;

            foreach (var field in text.Split('&'))
            {
                if (field.Length == 0) continue;
                var separator = field.IndexOf('=');
                var key = separator < 0 ? field : field.Substring(0, separator);
                var value = separator < 0 ? "" : field.Substring(separator + 1);
                values[DecodeQuery(key)] = DecodeQuery(value);
            }

            return values;
        }

        private static string DecodeQuery(string value) =>
            Uri.UnescapeDataString(value.Replace('+', ' '));

        private static string RandomBase64Url(int byteCount)
        {
            var bytes = new byte[byteCount];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return Base64Url(bytes);
        }

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
