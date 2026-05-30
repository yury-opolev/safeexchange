/// <summary>
/// SecretAdminDetailOutput — full metadata view returned by
/// GET v2/admin/secret/{secretName}. Extends the overview with audit fields.
/// Never exposes content bytes or chunk data.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class SecretAdminDetailOutput : SecretAdminOverviewOutput
    {
        /// <summary>UTC instant the secret was last modified.</summary>
        public DateTime ModifiedAt { get; set; }

        /// <summary>UPN / identity of the user who last modified the secret.</summary>
        public string ModifiedBy { get; set; } = string.Empty;

        /// <summary>Whether the secret is kept in storage even after expiry.</summary>
        public bool KeepInStorage { get; set; }

        /// <summary>The audit instance identifier when audit is enabled; empty string otherwise.</summary>
        public string AuditInstanceId { get; set; } = string.Empty;
    }
}
