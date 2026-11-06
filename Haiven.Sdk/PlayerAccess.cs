#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Haiven.Sdk.Rest;

namespace Haiven.Sdk
{
    /// <summary>Thin Phase 1 helpers for game access and player statuses.</summary>
    public static class PlayerAccess
    {
        /// <summary>The first published player-status code. Status codes remain an open vocabulary.</summary>
        public const string Founder = "founder";

        /// <summary>
        /// Reads whether the signed-in player may launch a game. A successful response with no
        /// body remains null, and REST failures pass through unchanged from the injected transport.
        /// </summary>
        public static Task<GameAccessResponse?> GetGameAsync(
            RestApi api,
            long gameId,
            CancellationToken cancellationToken = default)
        {
            if (api is null) throw new ArgumentNullException(nameof(api));
            return api.GetGamesGameIdAccessAsync(gameId, cancellationToken);
        }

        /// <summary>
        /// Finds an exact open status code without treating an unknown code as invalid.
        /// </summary>
        public static PlayerStatus? FindStatus(RewardEligibilityResponse? eligibility, string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            if (eligibility?.Statuses is null) return null;
            foreach (var status in eligibility.Statuses)
                if (string.Equals(status.Code, code, StringComparison.Ordinal)) return status;
            return null;
        }

        /// <summary>The founder status, independent of subscriber eligibility.</summary>
        public static PlayerStatus? FounderStatus(RewardEligibilityResponse? eligibility) =>
            FindStatus(eligibility, Founder);
    }
}
