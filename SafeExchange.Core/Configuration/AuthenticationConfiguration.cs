

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
    }
}
