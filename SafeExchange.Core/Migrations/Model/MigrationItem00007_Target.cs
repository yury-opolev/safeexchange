/// <summary>
/// MigrationItem00007
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using SafeExchange.Core.Model;
    using System;

    public class MigrationItem00007_Target
    {
        public MigrationItem00007_Target()
        { }

        public MigrationItem00007_Target(string userId, MigrationItem00007 source)
        {
            this.PartitionKey = PinnedGroup.DefaultPartitionKey;
            this.id = source.id;

            this.UserId = userId;
            this.GroupItemId = source.GroupId;
            this.CreatedAt = DateTime.UtcNow;
        }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string UserId { get; set; }

        public string GroupItemId { get; set; }

        public DateTime CreatedAt { get; set; }
    }
}
