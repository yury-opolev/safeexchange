/// <summary>
/// MigrationItem00006_2
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using SafeExchange.Core.Model;
    using System;

    public class MigrationItem00006_2
    {
        public MigrationItem00006_2()
        { }

        public MigrationItem00006_2(MigrationItem00006_2 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }
    }
}
