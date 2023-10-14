

namespace SafeExchange.Core.Model.Dto.Output
{
    using System;

    public class WebhookSubscriptionOverviewOutput
	{
        public bool Enabled { get; set; }

        public WebhookEventTypeOutput EventType { get; set; }

        public string Url { get; set; }
    }
}

