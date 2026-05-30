/// <summary>
/// SecretAdminOverviewOutput — lightweight projection returned by
/// GET v2/admin/secret-list. Contains only fields translatable by Cosmos EF,
/// so the query is fully server-side paginated. Never exposes content bytes or chunk data.
/// </summary>

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class SecretAdminOverviewOutput
    {
        /// <summary>The unique name (key) of the secret.</summary>
        public string ObjectName { get; set; } = string.Empty;

        /// <summary>UTC instant the secret was created.</summary>
        public DateTime CreatedAt { get; set; }

        /// <summary>UTC instant the secret was last read. Equal to CreatedAt when never accessed after creation.</summary>
        public DateTime LastAccessedAt { get; set; }
    }
}
