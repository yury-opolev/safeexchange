/// <summary>
/// TokenClassification — the single source of truth for distinguishing an
/// app-only (client-credentials) token from a delegated user token, based purely
/// on the presence of claims. AAD v1 app tokens carry appid/appidacr; AAD v2 app
/// tokens carry azp/azpacr. A delegated user token additionally carries a scope
/// claim (scp/scope). Classifying a v2 app principal as a user would let it bypass
/// the registered-application gate (CWE-287), so both <see cref="TokenHelper"/>
/// (live auth path) and the S2S issuer validator consume this helper to stay in
/// lock-step.
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using System;
    using System.Linq;

    public static class TokenClassification
    {
        private const string AppIdClaim = "appid";
        private const string AppIdAcrClaim = "appidacr";
        private const string AzpClaim = "azp";
        private const string AzpAcrClaim = "azpacr";

        private static readonly string[] DelegatedScopeClaims =
        {
            "http://schemas.microsoft.com/identity/claims/scope",
            "scope",
            "scp",
        };

        /// <summary>True when the token carries a full app-credential claim pair (v1 or v2).</summary>
        public static bool HasAppCredentialClaims(Func<string, bool> hasClaim)
        {
            ArgumentNullException.ThrowIfNull(hasClaim);

            return (hasClaim(AppIdClaim) && hasClaim(AppIdAcrClaim))
                || (hasClaim(AzpClaim) && hasClaim(AzpAcrClaim));
        }

        /// <summary>True when the token carries any delegated (user) scope claim.</summary>
        public static bool HasDelegatedScopeClaim(Func<string, bool> hasClaim)
        {
            ArgumentNullException.ThrowIfNull(hasClaim);

            return DelegatedScopeClaims.Any(hasClaim);
        }

        /// <summary>
        /// True for an app-only (client-credentials) token: it has app-credential
        /// claims and NO delegated scope claim.
        /// </summary>
        public static bool IsAppOnlyToken(Func<string, bool> hasClaim)
            => HasAppCredentialClaims(hasClaim) && !HasDelegatedScopeClaim(hasClaim);
    }
}
