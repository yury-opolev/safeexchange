/// <summary>
/// IWebhookNotificator
/// </summary>

namespace SafeExchange.Core.WebhookNotifications
{
    using SafeExchange.Core.Model;

    public interface IWebhookNotificator
	{
		public ValueTask TryNotifyAsync(AccessRequest accessRequest, WebhookSubscription webhookSubscription, IList<string> recipients);
	}
}

