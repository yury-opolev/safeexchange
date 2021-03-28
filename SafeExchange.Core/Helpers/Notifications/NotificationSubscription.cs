/// <summary>
/// SafeExchange
/// </summary>

namespace SpaceOyster.SafeExchange.Core
{
    using System;

    public class NotificationSubscription
    {
        public string id { get; set; }

        public string PartitionKey { get; set; }

        public string UserId { get; set; }

        public string Url { get; set; }

        public string P256dh { get; set; }

        public string Auth { get; set; }

        public override string ToString()
        {
            return $"Id:{this.id}, UserId:{this.UserId}, Endpoint:{this.Url?.Substring(0, 40)}...";
        }
    }
}
