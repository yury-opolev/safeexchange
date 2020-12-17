/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using Microsoft.Azure.Cosmos.Table;

    public class GroupDictionaryItem : TableEntity
    {
        public string GroupId { get; set; }

        public string GroupMail { get; set; }

        public bool ScheduleExpiration { get; set; }

        public DateTime ExpireAt { get; set; }
    }
}
