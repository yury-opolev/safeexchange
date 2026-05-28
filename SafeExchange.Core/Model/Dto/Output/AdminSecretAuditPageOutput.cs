/// <summary>
/// AdminSecretAuditPageOutput — admin-facing audit response keyed by
/// AuditInstanceId. Wraps SecretAuditPageOutput plus anchor metadata so the
/// admin UI can render the secret name / historical state without a second
/// round-trip.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class AdminSecretAuditPageOutput
    {
        public string AuditInstanceId { get; set; } = string.Empty;

        public string SecretObjectName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        public DateTime? DeletedAt { get; set; }

        public string? DeletedBy { get; set; }

        public DateTime? RetentionExpiresAt { get; set; }

        public bool IsHistorical { get; set; }

        public SecretAuditPageOutput Page { get; set; } = new();
    }
}
