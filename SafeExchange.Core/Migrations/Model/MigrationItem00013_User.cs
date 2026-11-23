/// <summary>
/// MigrationItem00013_User
/// </summary>

namespace SafeExchange.Core.Migrations
{
    /// <summary>
    /// Marker DTO used by the MCP flags backfill migration: only the fields needed to
    /// read and replace a document of the Users container.
    /// </summary>
    public class MigrationItem00013_User
    {
        public string PartitionKey { get; set; } = string.Empty;

        public string id { get; set; } = string.Empty;
    }
}
