/// <summary>
/// WebhookSubscriptionUpdateInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class WebhookSubscriptionUpdateInput
    {
        public bool? Enabled { get; set; }

        public bool? Authenticate { get; set; }

        public string? AuthenticationResource { get; set; }

        public TimeSpan? WebhookCallDelay { get; set; }

        public string? ContactEmail { get; set; }
    }
}
