/// <summary>
/// UserDetailOutput — full user detail returned by GET v2/admin/users/{upn}.
/// Groups are intentionally omitted.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

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
    }
}
