/// <summary>
/// MigrationItem00005
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00005
    {
        public MigrationItem00005()
        { }

        public MigrationItem00005(MigrationItem00005 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
