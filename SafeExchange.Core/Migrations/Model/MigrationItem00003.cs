/// <summary>
/// MigrationItem00003
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00003
    {
        public MigrationItem00003()
        { }

        public MigrationItem00003(MigrationItem00003 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
