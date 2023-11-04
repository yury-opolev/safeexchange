

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class WebhookSubscriptionOverviewOutput
	{
        public string Id { get; set; }

        public bool Enabled { get; set; }

        public WebhookEventTypeOutput EventType { get; set; }

        public string Url { get; set; }
    }
}

