/// <summary>
/// UserDetailOutput — full user detail returned by GET v2/admin/users/{upn}.
/// Groups are intentionally omitted.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// Full detail of a <see cref="SafeExchange.Core.Model.User"/> as returned by the admin user-detail endpoint.
    /// The <c>Groups</c> collection is intentionally excluded.
    /// </summary>
    public class UserDetailOutput
    {
        /// <summary>Gets or sets the Azure AD user principal name (UPN).</summary>
        public string AadUpn { get; set; } = string.Empty;

        /// <summary>Gets or sets the user's display name.</summary>
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>Gets or sets the contact e-mail address.</summary>
        public string ContactEmail { get; set; } = string.Empty;

        /// <summary>Gets or sets a value indicating whether the user account is enabled.</summary>
        public bool Enabled { get; set; }

        /// <summary>Gets or sets the Cosmos DB document id.</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>Gets or sets the Azure AD object id.</summary>
        public string AadObjectId { get; set; } = string.Empty;

        /// <summary>Gets or sets the Azure AD tenant id.</summary>
        public string AadTenantId { get; set; } = string.Empty;

        /// <summary>Gets or sets the UTC timestamp when the user record was created.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>Gets or sets the UTC timestamp when the user record was last modified.</summary>
        public DateTime ModifiedAt { get; set; }

        /// <summary>Gets or sets a value indicating whether the user receives external notifications.</summary>
        public bool ReceiveExternalNotifications { get; set; }

        /// <summary>Gets or sets a value indicating whether the user is required to consent in AAD.</summary>
        public bool ConsentRequired { get; set; }

        /// <summary>The user's current pseudonymous telemetry id (empty if never set).</summary>
        public string CurrentTelemetryId { get; set; } = string.Empty;

        /// <summary>UTC instant the current telemetry id was generated.</summary>
        public DateTime TelemetryIdActiveSinceUtc { get; set; }

        /// <summary>UTC instant the current telemetry id is due to rotate.</summary>
        public DateTime TelemetryIdRotatesAtUtc { get; set; }

        /// <summary>Recently retired telemetry ids still within the retention window,
        /// newest first.</summary>
        public List<TelemetryIdWindowOutput> RecentTelemetryIds { get; set; } = new();
    }
}
