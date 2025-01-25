/// <summary>
/// MigrationItem00007
/// </summary>

namespace SafeExchange.Core.Migrations
{
    using System;

    public class MigrationItem00007
    {
        public MigrationItem00007()
        { }

        public string PartitionKey { get; set; }

        public string id { get; set; }

        public string GroupId { get; set; }

        public string DisplayName { get; set; }
        
        public string? GroupMail { get; set; }
        
        public DateTime LastUsedAt { get; set; }
        
        public DateTime CreatedAt { get; set; }
        
        public string CreatedBy { get; set; }
    }
}
