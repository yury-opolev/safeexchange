/// <summary>
/// User
/// </summary>

namespace SafeExchange.Core.Model
{
    using System;

    public class User
    {
        public const string DefaultPartitionKey = "USER";

        public User() { }

        public User(string displayName, string aadObjectId, string aadTenantId, string aadUpn, string contactEmail)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = User.DefaultPartitionKey;
            this.Enabled = true;
            this.ReceiveExternalNotifications = true;

            this.DisplayName = displayName ?? throw new ArgumentNullException(nameof(displayName));
            this.AadObjectId = aadObjectId ?? throw new ArgumentNullException(nameof(aadObjectId));
            this.AadTenantId = aadTenantId ?? throw new ArgumentNullException(nameof(aadTenantId));
            this.AadUpn = aadUpn ?? throw new ArgumentNullException(nameof(aadUpn));

            this.ContactEmail = contactEmail ?? string.Empty;

            this.Groups = new List<UserGroup>();
            this.GroupSyncNotBefore = DateTime.MinValue;

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.ModifiedAt = DateTime.MinValue;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public bool Enabled { get; set; }

        public string DisplayName { get; set; }

        public string ContactEmail { get; set; }

        public string AadUpn { get; set; }

        public string AadObjectId { get; set; }

        public string AadTenantId { get; set; }

        public List<UserGroup> Groups { get; set; }

        public DateTime GroupSyncNotBefore { get; set; }

        public bool ReceiveExternalNotifications { get; set; }

        /// <summary>
        /// User is required to consent in AAD to the application to allow synchronization of user group memberships in Graph.
        /// </summary>
        public bool ConsentRequired { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ModifiedAt { get; set; }

        /// <summary>Current pseudonymous telemetry id (GUID "n"). Rotates weekly; stamped on
        /// telemetry instead of real identifiers. Empty until first set (migration/lazy).</summary>
        public string TelemetryId { get; set; } = string.Empty;

        /// <summary>UTC instant at/after which TelemetryId must rotate (next week boundary).</summary>
        public DateTime TelemetryIdExpiresAt { get; set; }

        /// <summary>UTC instant the current TelemetryId was generated. Used as the
        /// validFrom of a retired id when it rotates into the TelemetryIdMap.</summary>
        public DateTime TelemetryIdIssuedAt { get; set; }

        /// <summary>Maps to the Cosmos system-maintained <c>_etag</c>. Configured as an
        /// optimistic-concurrency token so concurrent writers to the same user document
        /// (e.g. several requests rotating the telemetry id at the same week boundary)
        /// serialize: exactly one write wins, the rest observe a concurrency conflict and
        /// re-read to adopt the winning state. Null only for an in-memory, not-yet-persisted
        /// user.</summary>
        public string? ETag { get; set; }
    }
}
