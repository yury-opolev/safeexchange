namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class SecretAuditAnchorOutput
    {
        public string AuditInstanceId { get; set; } = string.Empty;
        public string SecretObjectName { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime? DeletedAt { get; set; }
        public string? DeletedBy { get; set; }
        public DateTime? RetentionExpiresAt { get; set; }
        /// <summary>True iff the underlying secret has been purged but the audit anchor (and its events) are still retained.</summary>
        public bool IsHistorical { get; set; }
    }
}
