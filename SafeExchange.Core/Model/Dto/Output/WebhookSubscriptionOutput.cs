

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class WebhookSubscriptionOutput
	{
        public string Id { get; set; }

        public WebhookEventTypeOutput EventType { get; set; }

        public string Url { get; set; }

        public bool Enabled { get; set; }

        public bool Authenticate { get; set; }

        public string? AuthenticationResource { get; set; }

        public TimeSpan WebhookCallDelay { get; set; }

        public string ContactEmail { get; set; }
    }
}

