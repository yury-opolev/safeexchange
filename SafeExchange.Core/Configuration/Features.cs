/// <summary>
/// Features
/// </summary>

namespace SafeExchange.Core.Configuration
{
    using System;

    public class Features
    {
        public bool UseExternalWebHookNotifications { get; set; }

        public bool UseGroupsAuthorization { get; set; }

        public bool UseGraphUserSearch { get; set; }

        public bool UseGraphGroupSearch { get; set; }

        public bool AllowLegacyAttachmentUploads { get; set; } = true;

        public bool IgnoreChunkHashHeader { get; set; } = false;

        public bool UseAccessGiveUp { get; set; } = false;

        public int AuditRetentionDays { get; set; } = 365;

        /// <summary>
        /// Master flag for the spike's self-service S2S apps surface
        /// (POST/GET/DELETE/PATCH /s2sapps and its sub-routes). When false,
        /// the endpoints return 204/`disabled` so the client can hide the UI.
        /// </summary>
        public bool S2SAppsSelfService { get; set; } = false;

        /// <summary>
        /// Enforce the ApplicationOwner invariant on register / owner-remove
        /// (≥2 distinct principals, ≥1 of which is a User). Off by default so
        /// legacy admin-created apps without owners aren't broken on rollout —
        /// the migration class sets owners + a follow-up flips this to true.
        /// </summary>
        public bool RequireApplicationOwnership { get; set; } = false;

        /// <summary>
        /// When true, email/UPN-shaped substrings are redacted from trace and exception
        /// telemetry message text by <c>PiiRedactionTelemetryInitializer</c>.
        /// Can be toggled without redeploy (read live via IOptionsMonitor).
        /// </summary>
        public bool RedactTelemetryPii { get; set; } = false;

        public Features Clone() => new Features()
        {
            UseExternalWebHookNotifications = this.UseExternalWebHookNotifications,
            UseGroupsAuthorization = this.UseGroupsAuthorization,
            UseGraphUserSearch = this.UseGraphUserSearch,
            UseGraphGroupSearch = this.UseGraphGroupSearch,
            AllowLegacyAttachmentUploads = this.AllowLegacyAttachmentUploads,
            IgnoreChunkHashHeader = this.IgnoreChunkHashHeader,
            UseAccessGiveUp = this.UseAccessGiveUp,
            AuditRetentionDays = this.AuditRetentionDays,
            S2SAppsSelfService = this.S2SAppsSelfService,
            RequireApplicationOwnership = this.RequireApplicationOwnership,
            RedactTelemetryPii = this.RedactTelemetryPii,
        };

        public override bool Equals(object obj)
        {
            if (obj is not Features other)
            {
                return false;
            }

            return
                this.UseExternalWebHookNotifications.Equals(other.UseExternalWebHookNotifications) &&
                this.UseGroupsAuthorization.Equals(other.UseGroupsAuthorization) &&
                this.UseGraphUserSearch.Equals(other.UseGraphUserSearch) &&
                this.UseGraphGroupSearch.Equals(other.UseGraphGroupSearch) &&
                this.AllowLegacyAttachmentUploads.Equals(other.AllowLegacyAttachmentUploads) &&
                this.IgnoreChunkHashHeader.Equals(other.IgnoreChunkHashHeader) &&
                this.UseAccessGiveUp.Equals(other.UseAccessGiveUp) &&
                this.AuditRetentionDays.Equals(other.AuditRetentionDays) &&
                this.S2SAppsSelfService.Equals(other.S2SAppsSelfService) &&
                this.RequireApplicationOwnership.Equals(other.RequireApplicationOwnership) &&
                this.RedactTelemetryPii.Equals(other.RedactTelemetryPii);
        }

        public override int GetHashCode() => HashCode.Combine(
            HashCode.Combine(
                this.UseExternalWebHookNotifications, this.UseGroupsAuthorization,
                this.UseGraphUserSearch, this.UseGraphGroupSearch,
                this.AllowLegacyAttachmentUploads, this.IgnoreChunkHashHeader,
                this.UseAccessGiveUp, this.AuditRetentionDays),
            HashCode.Combine(this.S2SAppsSelfService, this.RequireApplicationOwnership,
                this.RedactTelemetryPii));
    }
}
