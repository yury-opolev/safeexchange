

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using System;

    [Index(nameof(EventType), nameof(EventId), IsUnique = true)]
    public class WebhookNotificationData
	{
        public const string DefaultPartitionKey = "WEBHOOKNDAT";

        public WebhookNotificationData() { }

        public WebhookNotificationData(WebhookEventType eventType, string eventId)
            : this(eventType, eventId, string.Empty)
        {
        }

        public WebhookNotificationData(WebhookEventType eventType, string eventId, string eventPayload)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = WebhookSubscription.DefaultPartitionKey;

            this.EventType = eventType;
            this.EventId = eventId;

            this.EventPayload = eventPayload;

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.ExpireAt = DateTime.MaxValue;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public WebhookEventType EventType { get; set; }

        public string EventId { get; set; }

        public string EventPayload { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpireAt { get; set; }
    }
}
