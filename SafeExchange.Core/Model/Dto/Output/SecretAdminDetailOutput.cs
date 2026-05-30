/// <summary>
/// SecretAdminDetailOutput — full metadata view returned by
/// GET v2/admin/secret/{secretName}. Contains all detail fields including
/// audit, expiry, and access metadata. Never exposes content bytes or chunk data.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;
    using System.Collections.Generic;

    public class SecretAdminDetailOutput
    {
        /// <summary>The unique name (key) of the secret.</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>UPN / identity of the user who created the secret.</summary>
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>UTC instant the secret was created.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>UTC instant the secret was last read. Equal to CreatedAt when never accessed after creation.</summary>
        public DateTime LastAccessedAt { get; set; }

        /// <summary>UTC instant the secret was last modified.</summary>
        public DateTime ModifiedAt { get; set; }

        /// <summary>UPN / identity of the user who last modified the secret.</summary>
        public string ModifiedBy { get; set; } = string.Empty;

        /// <summary>
        /// Scheduled expiration time, or null when expiration is not configured
        /// (i.e. ExpirationMetadata.ScheduleExpiration is false or ExpireAt is default).
        /// </summary>
        public DateTime? ExpiresAt { get; set; }

        /// <summary>
        /// Idle-expiry deadline, or null when idle-expiry is not configured
        /// (i.e. ExpireOnIdleTime is false or ExpireIfUnusedAt is default).
        /// </summary>
        public DateTime? IdleDeleteAt { get; set; }

        /// <summary>
        /// Number of non-main content items (i.e. attachments).
        /// Computed server-side from the owned collection.
        /// </summary>
        public int AttachmentCount { get; set; }

        /// <summary>User-defined tags attached to the secret.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>Whether audit logging is enabled for this secret.</summary>
        public bool AuditEnabled { get; set; }

        /// <summary>The audit instance identifier when audit is enabled; empty string otherwise.</summary>
        public string AuditInstanceId { get; set; } = string.Empty;

        /// <summary>Whether the secret is kept in storage even after expiry.</summary>
        public bool KeepInStorage { get; set; }
    }
}
