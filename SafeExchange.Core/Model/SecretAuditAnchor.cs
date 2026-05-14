/// <summary>
/// SecretAuditAnchor — one anchor per audit-enabled secret. Outlives ObjectMetadata
/// so that audit events of deleted secrets remain reachable until retention expires.
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public class SecretAuditAnchor
    {
        public SecretAuditAnchor() { }

        public SecretAuditAnchor(string auditInstanceId, string secretObjectName, string createdBy)
        {
            this.AuditInstanceId = auditInstanceId ?? throw new ArgumentNullException(nameof(auditInstanceId));
            this.SecretObjectName = secretObjectName ?? throw new ArgumentNullException(nameof(secretObjectName));
            this.CreatedAt = DateTimeProvider.UtcNow;
            this.CreatedBy = createdBy ?? string.Empty;
        }

        public string AuditInstanceId { get; set; } = string.Empty;

        public string SecretObjectName { get; set; } = string.Empty;

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; } = string.Empty;

        public DateTime? DeletedAt { get; set; }

        public string? DeletedBy { get; set; }

        public DateTime? RetentionExpiresAt { get; set; }
    }
}
