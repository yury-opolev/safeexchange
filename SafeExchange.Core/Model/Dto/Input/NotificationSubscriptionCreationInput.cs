/// <summary>
/// NotificationSubscriptionCreationInput
/// </summary>

namespace SafeExchange.Core.Model.Dto.Input
{
    using System;

    public class NotificationSubscriptionCreationInput
    {
        public string Url { get; set; }

        public string P256dh { get; set; }

        public string Auth { get; set; }
    }
}
