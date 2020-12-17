/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using Microsoft.Azure.Cosmos.Table;

    public class ObjectMetadata : TableEntity
    {
        public string SecretName { get; set; }

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; }

        public DateTime ModifiedAt { get; set; }

        public string ModifiedBy { get; set; }

        public bool DestroyAfterRead { get; set; }

        public bool ScheduleDestroy { get; set; }
 
        public DateTime DestroyAt { get; set; }
    }
}