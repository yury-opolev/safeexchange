/// <summary>
/// MigrationItem00004
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00004_1
    {
        public MigrationItem00004_1()
        { }

        public MigrationItem00004_1(MigrationItem00004_1 source)
        {
            this.PartitionKey = source.PartitionKey;
            this.id = source.id;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string GroupMail { get; set; }
    }
}
