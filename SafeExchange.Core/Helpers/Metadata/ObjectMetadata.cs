/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class ObjectMetadata
    {
        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; }

        public DateTime ModifiedAt { get; set; }

        public string ModifiedBy { get; set; }

        public bool DestroyAfterRead { get; set; }

        public bool ScheduleDestroy { get; set; }
 
        public DateTime DestroyAt { get; set; }
    }
}