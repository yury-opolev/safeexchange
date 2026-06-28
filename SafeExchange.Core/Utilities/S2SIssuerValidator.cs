/// <summary>
/// S2SIssuerValidator — token-kind-aware issuer validation backing the cross-tenant
/// S2S feature. The configured (home-tenant) issuers are accepted for BOTH user and
/// app tokens — unchanged behavior. Issuers derived from the S2S allowlist are
/// accepted ONLY for app-only (client-credentials) tokens, so a user signing in from
/// an allowlisted tenant is still rejected. Because the AAD `iss` claim is part of the
/// signed token, this allowlist is a sound tenant gate on top of signature + audience
/// + the (clientId, tenantId) registration check.
/// </summary>

namespace SafeExchange.Core.Utilities
{
    using Microsoft.IdentityModel.Tokens;
    using SafeExchange.Core.Configuration;
    using System;
    using System.Collections.Generic;

    public sealed class S2SIssuerValidator
    {
        private readonly HashSet<string> configuredIssuers;
        private readonly HashSet<string> s2sIssuers;

        public S2SIssuerValidator(IEnumerable<string> configuredIssuers, IReadOnlyList<S2SAllowedTenant> allowedTenants)
        {
            ArgumentNullException.ThrowIfNull(configuredIssuers);

            this.configuredIssuers = new HashSet<string>(configuredIssuers, StringComparer.OrdinalIgnoreCase);
            this.s2sIssuers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var tenant in allowedTenants ?? Array.Empty<S2SAllowedTenant>())
            {
                // Both the AAD v2 and v1 issuer forms for the tenant.
                this.s2sIssuers.Add($"https://login.microsoftonline.com/{tenant.TenantId}/v2.0");
                this.s2sIssuers.Add($"https://sts.windows.net/{tenant.TenantId}/");
            }
        }

        /// <summary>The derived set of issuers accepted for app-only tokens (test/diagnostic visibility).</summary>
        public IReadOnlyCollection<string> S2SIssuers => this.s2sIssuers;

        /// <summary>
        /// Returns <paramref name="issuer"/> when valid for the given token kind;
        /// throws <see cref="SecurityTokenInvalidIssuerException"/> otherwise. The
        /// message is intentionally generic — the auth middleware never echoes it to
        /// the caller (CWE-209).
        /// </summary>
        public string Validate(string issuer, bool isAppOnlyToken)
        {
            if (this.configuredIssuers.Contains(issuer))
            {
                return issuer;
            }

            if (isAppOnlyToken && this.s2sIssuers.Contains(issuer))
            {
                return issuer;
            }

            throw new SecurityTokenInvalidIssuerException("Issuer is not valid for this token kind.")
            {
                InvalidIssuer = issuer,
            };
        }
    }
}
