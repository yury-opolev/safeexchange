/// <summary>
/// MigrationItem00009
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    /// <summary>
    /// Marker migration for addition of <see cref="SafeExchange.Core.Model.ObjectMetadata.Tags"/>
    /// to the ObjectMetadata container.
    ///
    /// The new field is a list that defaults to empty on existing documents, so the Cosmos DB
    /// provider handles the schema change lazily; this migration enumerates ObjectMetadata
    /// documents and logs their ids so that the addition is recorded in the operations log.
    /// </summary>
    public class MigrationItem00009
    {
        public MigrationItem00009()
        { }

        public MigrationItem00009(MigrationItem00009 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
