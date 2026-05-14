/// <summary>
/// MigrationItem00010
/// </summary>

namespace SafeExchange.Core.Migrations
{
    /// <summary>
    /// Marker class used by the audit-fields backfill migration. Pre-feature
    /// ObjectMetadata documents in Cosmos have no <c>AuditEnabled</c> field; this
    /// migration writes <c>AuditEnabled = false</c> and <c>AuditInstanceId = null</c>
    /// onto every such document so the fields are uniformly present in storage.
    /// </summary>
    public class MigrationItem00010
    {
        public MigrationItem00010()
        { }

        public MigrationItem00010(MigrationItem00010 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
