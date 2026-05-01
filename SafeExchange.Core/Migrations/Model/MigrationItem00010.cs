/// <summary>
/// MigrationItem00010
/// </summary>

namespace SafeExchange.Core.Migrations
{
    /// <summary>
    /// Marker for the Tags-backfill migration. Pre-feature ObjectMetadata documents
    /// in Cosmos have no <c>Tags</c> field; this migration writes <c>Tags = []</c>
    /// onto every such document so the field is uniformly present in storage.
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
