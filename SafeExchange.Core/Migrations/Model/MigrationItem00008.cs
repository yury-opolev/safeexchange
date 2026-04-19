/// <summary>
/// MigrationItem00008
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    /// <summary>
    /// Marker migration for addition of <see cref="SafeExchange.Core.Model.ContentMetadata.Hash"/>
    /// and <see cref="SafeExchange.Core.Model.ContentMetadata.RunningHashState"/> properties to
    /// embedded ContentMetadata documents in the ObjectMetadata container.
    ///
    /// Both new fields are nullable and default to null on existing documents, so the Cosmos DB
    /// provider handles the schema change lazily; this migration enumerates ObjectMetadata
    /// documents and logs their ids so that the addition is recorded in the operations log.
    /// </summary>
    public class MigrationItem00008
    {
        public MigrationItem00008()
        { }

        public MigrationItem00008(MigrationItem00008 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
