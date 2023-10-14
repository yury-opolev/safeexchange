

namespace SafeExchange.Core.Model
{
    using Microsoft.EntityFrameworkCore;
    using SafeExchange.Core.Model.Dto.Input;
    using System;

    [Index(nameof(EventType), nameof(Url), IsUnique = true)]
    public class WebhookSubscription
	{
        public const string DefaultPartitionKey = "WEBHOOKSUB";

        public WebhookSubscription() { }

        public WebhookSubscription(WebhookSubscriptionCreationInput input, string createdBy)
        {
            if (input == default)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (string.IsNullOrEmpty(createdBy))
            {
                throw new ArgumentNullException(nameof(createdBy));
            }

            this.Id = Guid.NewGuid().ToString();
            this.PartitionKey = WebhookSubscription.DefaultPartitionKey;

            this.Enabled = input.Enabled;
            this.EventType = input.EventType.ToModel();
            this.Url = input.Url;
            this.Authenticate = input.Authenticate;
            if (input.Authenticate && string.IsNullOrEmpty(input.AuthenticationResource))
            {
                throw new ArgumentNullException(nameof(input.AuthenticationResource));
            }

            this.AuthenticationResource = input.AuthenticationResource;
            this.WebhookCallDelay = input.WebhookCallDelay;

            this.ContactEmail = input.ContactEmail ?? throw new ArgumentNullException(nameof(input.ContactEmail));

            this.CreatedAt = DateTimeProvider.UtcNow;
            this.CreatedBy = createdBy;
            this.ModifiedAt = DateTime.MinValue;
            this.ModifiedBy = string.Empty;
        }

        public string Id { get; set; }

        public string PartitionKey { get; set; }

        public bool Enabled { get; set; }

        public WebhookEventType EventType { get; set; }

        public string Url { get; set; }

        public bool Authenticate { get; set; }

        public string? AuthenticationResource { get; set; }

        public TimeSpan WebhookCallDelay { get; set; }

        public string ContactEmail { get; set; }

        public DateTime CreatedAt { get; set; }

        public string CreatedBy { get; set; }

        public DateTime ModifiedAt { get; set; }

        public string ModifiedBy { get; set; }
    }
}
