using System;
using SafeExchange.Core.Model;

namespace SafeExchange.Core.WebhookNotifications
{
	public class NullWebhookNotificator : IWebhookNotificator
	{
        public ValueTask TryNotifyAsync(AccessRequest accessRequest, WebhookSubscription webhookSubscription, IList<string> recipients)
        {
            // no-op
            return ValueTask.CompletedTask;
        }
    }
}

