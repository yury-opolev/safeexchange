

namespace SafeExchange.Core.Configuration
{
    using System;

    /// <summary>
    /// Binds to the <c>Authentication</c> configuration section. All validation
    /// flags default to <c>true</c> — the secure posture — so that a missing or
    /// mistyped config value cannot silently disable audience / issuer validation.
    /// <see cref="TokenValidationParametersProvider"/> refuses to start if
    /// validation is disabled or if the corresponding allowlist is empty.
    /// </summary>
    public class AuthenticationConfiguration
    {
        public string Authority { get; set; } = string.Empty;

        public string MetadataAddress { get; set; } = string.Empty;

        public bool ValidateAudience { get; set; } = true;

        public string ValidAudiences { get; set; } = string.Empty;

        public bool ValidateIssuer { get; set; } = true;

        public string ValidIssuers { get; set; } = string.Empty;

        /// <summary>
        /// Optional JSON array of tenants from which APP-ONLY (S2S / client-credentials)
        /// tokens are accepted, IN ADDITION to the configured home tenant. Each entry is
        /// <c>{ "tenantId": "&lt;guid&gt;", "displayName": "&lt;label&gt;" }</c>. Empty /
        /// missing ⇒ the feature is off and token validation is byte-for-byte unchanged.
        /// User (delegated) tokens are unaffected — they are still accepted only from the
        /// configured home tenant. Parsed by <see cref="S2SAllowedTenant.ParseList"/>.
        /// </summary>
        public string S2SAllowedTenants { get; set; } = string.Empty;

        /// <summary>
        /// Comma-separated list of claim types, in order of preference, that
        /// <see cref="TokenHelper.GetUpn"/> will try when resolving the caller's
        /// UPN. The first non-empty claim wins.
        ///
        /// Defaults to the safe pair — the raw <c>upn</c> claim and its
        /// <see cref="System.Security.Claims.ClaimTypes.Upn"/> mapped form
        /// (<c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn</c>).
        /// The older <c>preferred_username</c> / <c>email</c> fallbacks are
        /// intentionally excluded because those claims are not guaranteed
        /// stable or unique across a tenant and could alias a permission row
        /// onto a different principal than the grantor intended.
        ///
        /// Operators who need the legacy fallback chain — e.g. for a tenant
        /// configured to emit <c>email</c> but not <c>upn</c> — can override
        /// by setting, for example,
        /// <c>Authentication:UpnClaims = "upn,http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn,preferred_username,email,http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress"</c>.
        /// This is a safety valve so the tighter default can be relaxed
        /// without a code change if it breaks a specific deployment.
        /// </summary>
        public string UpnClaims { get; set; } =
            "upn,http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn";
    }
}
