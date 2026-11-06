namespace Haiven.Sdk
{
    /// <summary>The host action required after an authenticated transport result.</summary>
    public enum HaivenCredentialAction
    {
        Retain,
        Clear,
        RetryFreshSocket,
    }

    /// <summary>
    /// ADR 0041 credential-lifecycle rules, kept transport-neutral so engine
    /// adapters and games make the same retention decision.
    /// </summary>
    public static class HaivenCredentialLifecycle
    {
        public static HaivenCredentialAction ForHttpStatus(int statusCode) =>
            statusCode == 401 ? HaivenCredentialAction.Clear : HaivenCredentialAction.Retain;

        public static HaivenCredentialAction ForSocketClose(
            HaivenSessionState stateBeforeClose,
            int code,
            string? reason)
        {
            if (code != 1008) return HaivenCredentialAction.Retain;
            if (string.Equals(reason, "credential_revoked", System.StringComparison.Ordinal))
                return HaivenCredentialAction.Clear;
            if (stateBeforeClose == HaivenSessionState.Pending
                && string.Equals(reason, "unauthorized", System.StringComparison.Ordinal))
                return HaivenCredentialAction.Clear;
            return HaivenCredentialAction.Retain;
        }

        /// <summary>
        /// A live <c>auth.failure</c> refuses only same-socket replacement. ADR 0041
        /// treats <c>invalid_token</c> and <c>unauthorized</c> as one
        /// retry-then-clear rule: close that socket and retry the credential as
        /// first-frame authentication on a fresh socket, clearing it only if that
        /// handshake closes with <c>1008 unauthorized</c>.
        /// </summary>
        public static HaivenCredentialAction ForAuthenticationFailure(string? code) =>
            string.Equals(code, "invalid_token", System.StringComparison.Ordinal)
                || string.Equals(code, "unauthorized", System.StringComparison.Ordinal)
                ? HaivenCredentialAction.RetryFreshSocket
                : HaivenCredentialAction.Retain;
    }
}
