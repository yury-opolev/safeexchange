/// <summary>
/// SecretAdminOverviewOutput — lightweight projection returned by
/// GET v2/admin/secret-list. Never exposes content bytes or chunk data.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;
    using System.Collections.Generic;

    public class SecretAdminOverviewOutput
    {
        /// <summary>The unique name (key) of the secret.</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>UPN / identity of the user who created the secret.</summary>
        public string CreatedBy { get; set; } = string.Empty;

        /// <summary>UTC instant the secret was created.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>UTC instant the secret was last read. Equal to CreatedAt when never accessed after creation.</summary>
        public DateTime LastAccessedAt { get; set; }

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
        /// Computed client-side because Cosmos EF cannot translate
        /// owned-collection Count with a predicate.
        /// </summary>
        public int AttachmentCount { get; set; }

        /// <summary>User-defined tags attached to the secret.</summary>
        public List<string> Tags { get; set; } = new();

        /// <summary>Whether audit logging is enabled for this secret.</summary>
        public bool AuditEnabled { get; set; }
    }
}
