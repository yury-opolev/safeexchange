

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using System;
    using System.ComponentModel.DataAnnotations;

    [Index(nameof(WebhookSubscriptionId), nameof(EventType), nameof(EventId), IsUnique = true)]
    public class WebhookNotificationData
	{
        public const string DefaultPartitionKey = "WEBHOOKNDAT";

        public WebhookNotificationData() { }

        public WebhookNotificationData(string webhookSubscriptionId, WebhookEventType eventType, string eventId)
            : this(webhookSubscriptionId, eventType, eventId, string.Empty)
        {
        }

        public WebhookNotificationData(string webhookSubscriptionId, WebhookEventType eventType, string eventId, string eventPayload)
        {
            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = WebhookSubscription.DefaultPartitionKey;

            this.WebhookSubscriptionId = webhookSubscriptionId;
            this.EventType = eventType;
            this.EventId = eventId;

            this.EventPayload = eventPayload;

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.ExpireAt = this.CreatedAt + TimeSpan.FromDays(7); // TODO: make a setting
        }

        [Key]
        [Required]
        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public string WebhookSubscriptionId { get; set; }

        public WebhookEventType EventType { get; set; }

        public string EventId { get; set; }

        public string EventPayload { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime ExpireAt { get; set; }
    }
}
