/// <summary>
/// WebhookSubscriptionDeletionInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class WebhookSubscriptionDeletionInput
    {
        public WebhookEventTypeInput EventType { get; set; }

        public string Url { get; set; }
    }
}
