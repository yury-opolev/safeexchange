/// <summary>
/// MigrationItem00002
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00002
    {
        public MigrationItem00002()
        { }

        public MigrationItem00002(MigrationItem00002 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
